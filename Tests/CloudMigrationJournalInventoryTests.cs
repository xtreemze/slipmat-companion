using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed partial class CloudMigrationJournalTests
{
    [Fact]
    public async Task CompleteInventoryReturnsExactWholeJobRevisions()
    {
        using var directory = new JournalDirectory();
        await using var lease = await new CloudMigrationJournal(directory.Path).AcquireAsync();
        Assert.Empty(await lease.ReadAllAsync());
        var first = await lease.CompareExchangeAsync(null, Job());
        var second = await lease.CompareExchangeAsync(null, Job());
        Directory.CreateDirectory(Path.Combine(directory.Path, "sidecars"));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, ".uncommitted.tmp"), "scratch");
        var inventory = await lease.ReadAllAsync();
        Assert.Equal(new[] { first, second }.OrderBy(x => x.Job.Id), inventory);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("null")]
    [InlineData("mismatched-id")]
    [InlineData("invalid-name")]
    [InlineData("directory")]
    [InlineData("symlink")]
    [InlineData("oversize")]
    [InlineData("empty")]
    [InlineData("uppercase-extension")]
    [InlineData("zero-id")]
    [InlineData("partial-job")]
    [InlineData("duplicate-property")]
    [InlineData("null-binding")]
    [InlineData("empty-binding")]
    [InlineData("relative-path")]
    public async Task CompleteInventoryNeverOmitsAnUnusableJob(string failure)
    {
        using var directory = new JournalDirectory();
        await using var lease = await new CloudMigrationJournal(directory.Path).AcquireAsync();
        var valid = await lease.CompareExchangeAsync(null, Job());
        var badId = Guid.NewGuid().ToString("N");
        var bad = Path.Combine(directory.Path, badId + ".json");
        switch (failure)
        {
            case "empty-binding": await File.WriteAllTextAsync(bad,
                JsonSerializer.Serialize(valid.Job with { Id = badId, ProfileId = " " }, new JsonSerializerOptions(JsonSerializerDefaults.Web))); break;
            case "relative-path": await File.WriteAllTextAsync(bad,
                JsonSerializer.Serialize(valid.Job with { Id = badId, LocalPath = "relative/source" }, new JsonSerializerOptions(JsonSerializerDefaults.Web))); break;
            case "partial-job": await File.WriteAllTextAsync(bad, JsonSerializer.Serialize(new { id = badId })); break;
            case "duplicate-property":
                var raw = JsonSerializer.Serialize(valid.Job with { Id = badId }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                await File.WriteAllTextAsync(bad, "{\"id\":\"" + badId + "\"," + raw[1..]); break;
            case "null-binding": await File.WriteAllTextAsync(bad,
                JsonSerializer.Serialize(valid.Job with { Id = badId, LocalPath = null! }, new JsonSerializerOptions(JsonSerializerDefaults.Web))); break;
            case "empty": await File.WriteAllTextAsync(bad, ""); break;
            case "uppercase-extension": bad = Path.ChangeExtension(bad, ".JSON"); await File.WriteAllTextAsync(bad, "{}"); break;
            case "zero-id": bad = Path.Combine(directory.Path, Guid.Empty.ToString("N") + ".json"); await File.WriteAllTextAsync(bad, "{}"); break;
            case "corrupt": await File.WriteAllTextAsync(bad, "{"); break;
            case "null": await File.WriteAllTextAsync(bad, "null"); break;
            case "mismatched-id": await File.WriteAllTextAsync(bad, JsonSerializer.Serialize(Job())); break;
            case "invalid-name": bad = Path.Combine(directory.Path, "unexpected.json"); await File.WriteAllTextAsync(bad, "{}"); break;
            case "directory": Directory.CreateDirectory(bad); break;
            case "symlink": File.CreateSymbolicLink(bad, Path.Combine(directory.Path, valid.Job.Id + ".json")); break;
            case "oversize": await File.WriteAllBytesAsync(bad, new byte[4 * 1024 * 1024 + 1]); break;
        }
        var error = await Assert.ThrowsAnyAsync<Exception>(() => lease.ReadAllAsync());
        if (failure is "oversize" or "empty")
            Assert.Equal("migration-journal-inventory-limit", Assert.IsType<IOException>(error).Message);
        Assert.True(File.Exists(bad) || Directory.Exists(bad));
        Assert.Equal(valid, await lease.ReadAsync(valid.Job.Id));
    }

    [Fact]
    public async Task CompleteInventoryHonorsCancellationAndDisposedLease()
    {
        using var directory = new JournalDirectory();
        var lease = await new CloudMigrationJournal(directory.Path).AcquireAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.ReadAllAsync(new CancellationToken(true)));
        await lease.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lease.ReadAllAsync());
    }
    [Fact]
    public async Task CompleteInventoryRefusesTooManyJobEntriesBeforeReadingThem()
    {
        using var directory = new JournalDirectory();
        await using var lease = await new CloudMigrationJournal(directory.Path).AcquireAsync();
        for (var index = 0; index < 4097; index++)
            File.WriteAllText(Path.Combine(directory.Path, Guid.NewGuid().ToString("N") + ".json"), "{}");
        var error = await Assert.ThrowsAsync<IOException>(() => lease.ReadAllAsync());
        Assert.Equal("migration-journal-inventory-limit", error.Message);
        Assert.Equal(4097, Directory.EnumerateFiles(directory.Path, "*.json").Count());
    }

    [Fact]
    public async Task CompleteInventoryAcceptsLegacyOptionalFieldsAbsentAndEmptyRemoteRoot()
    {
        using var directory = new JournalDirectory();
        var job = Job() with { RemotePath = "" };
        var document = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(job,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.AsObject();
        string[] required = ["id", "profileId", "libraryName", "direction", "phase", "code", "localPath",
            "stagingPath", "backupPath", "remoteName", "remotePath", "previousProjectionPath",
            "previousEnabled", "verification", "createdAt", "updatedAt"];
        foreach (var key in document.Select(x => x.Key).Except(required).ToArray()) document.Remove(key);
        var raw = System.Text.Encoding.UTF8.GetBytes(document.ToJsonString());
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, job.Id + ".json"), raw);
        await using var lease = await new CloudMigrationJournal(directory.Path).AcquireAsync();
        var observed = Assert.Single(await lease.ReadAllAsync());
        Assert.Equal(job, observed.Job);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(raw)), observed.Revision);
    }

}
