using System.Linq;
using Jellyfin.Plugin.AudioGateway.Api;
using Jellyfin.Plugin.AudioGateway.Models;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class EventsControllerTests
{
    [Fact]
    public void ParseInclude_CommaSeparatedTokens_ReturnsExpectedFlags()
    {
        var flags = EventsController.ParseInclude("waveform, sidecar,analysis");

        Assert.True(flags.HasFlag(IncludeFlags.Waveform));
        Assert.True(flags.HasFlag(IncludeFlags.Sidecar));
        Assert.True(flags.HasFlag(IncludeFlags.Analysis));
    }

    [Fact]
    public void ParseInclude_UnknownTokens_AreIgnored()
    {
        var flags = EventsController.ParseInclude("waveform,wat,sidecar");

        Assert.True(flags.HasFlag(IncludeFlags.Waveform));
        Assert.True(flags.HasFlag(IncludeFlags.Sidecar));
        Assert.False(flags.HasFlag(IncludeFlags.Analysis));
    }

    [Fact]
    public void ParseInclude_Empty_ReturnsNone()
        => Assert.Equal(IncludeFlags.None, EventsController.ParseInclude(string.Empty));

    [Theory]
    [InlineData(typeof(EventsController))]
    [InlineData(typeof(AtlasController))]
    [InlineData(typeof(AtlasRequestsController))]
    [InlineData(typeof(StreamripController))]
    public void Controllers_RequireAuthentication(Type controllerType)
    {
        var authorize = controllerType.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true);
        Assert.NotEmpty(authorize);

        var allowAnonymous = controllerType.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true);
        Assert.Empty(allowAnonymous);
    }

    [Fact]
    public void AtlasRequestsController_Methods_AreNotAnonymous()
    {
        var methods = typeof(AtlasRequestsController)
            .GetMethods()
            .Where(method => method.DeclaringType == typeof(AtlasRequestsController));

        foreach (var method in methods)
        {
            Assert.Empty(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
        }
    }
}
