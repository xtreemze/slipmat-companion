using System.Reflection;
using Jellyfin.Plugin.AudioGateway.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class CloudMaintenanceAdmissionResolverTests
{
    [Fact]
    public void ResolvesOptionsFromTheActualCollectionItem()
    {
        var (library, fake) = Create();
        var result = CloudMaintenanceAdmissionResolver.Resolve(library, "Music", "/mnt/T7/Music");
        Assert.Equal(fake.Item.Id, result.LibraryId);
        Assert.Equal("/jellyfin/root/Music/options.xml", result.OptionsPath);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("id")]
    [InlineData("location")]
    [InlineData("item")]
    [InlineData("path")]
    [InlineData("shared-location")]
    [InlineData("nested-location")]
    public void RejectsAmbiguousOrChangedBinding(string change)
    {
        var (library, fake) = Create();
        switch (change)
        {
            case "missing": fake.Folders.Clear(); break;
            case "duplicate": fake.Folders.Add(fake.Folders[0]); break;
            case "id": fake.Folders[0].ItemId = Guid.Empty.ToString("N"); break;
            case "location": fake.Folders[0].Locations = ["/other"]; break;
            case "item": fake.Item = null!; break;
            case "path": fake.Item.Path = "/jellyfin/root/Other"; break;
            case "shared-location": fake.Folders.Add(new VirtualFolderInfo { Name = "Other", Locations = ["/mnt/T7/Music"] }); break;
            case "nested-location": fake.Folders.Add(new VirtualFolderInfo { Name = "Other", Locations = ["/mnt/T7/Music/Other"] }); break;
        }
        Assert.Throws<InvalidOperationException>(() => CloudMaintenanceAdmissionResolver.Resolve(library, "Music", "/mnt/T7/Music"));
    }

    private static (ILibraryManager, FakeLibrary) Create()
    {
        var manager = DispatchProxy.Create<ILibraryManager, FakeLibrary>();
        var fake = (FakeLibrary)(object)manager;
        fake.Item = new CollectionFolder { Id = Guid.NewGuid(), Path = "/jellyfin/root/Music" };
        fake.Folders = [new VirtualFolderInfo { Name = "Music", ItemId = fake.Item.Id.ToString("N"), Locations = ["/mnt/T7/Music"] }];
        return (manager, fake);
    }

    public class FakeLibrary : DispatchProxy
    {
        public List<VirtualFolderInfo> Folders { get; set; } = [];
        public CollectionFolder Item { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                "GetVirtualFolders" => Folders,
                "GetItemById" => Item,
                _ => throw new NotSupportedException(targetMethod?.Name),
            };
    }
}
