using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class RemoteCommandGuardTests
{
    private static readonly RemoteCommandGuard.Target Target =
        new("owner", "client", "generation-2", true, true, false);

    private static RemoteCommandGuard.Request Command(
        string action = "pause",
        string owner = "owner",
        string client = "client",
        string generation = "generation-2",
        string commandId = "cmd-1")
        => new(owner, client, generation, commandId, action);

    [Fact]
    public void AllowsSupportedTransportCommand()
        => Assert.Equal(RemoteCommandGuard.Rejection.None,
            RemoteCommandGuard.Evaluate(Command(), Target));

    [Fact]
    public void PreventsCrossUserOrCrossClientControl()
    {
        Assert.Equal(RemoteCommandGuard.Rejection.Forbidden,
            RemoteCommandGuard.Evaluate(Command(owner: "other"), Target));
        Assert.Equal(RemoteCommandGuard.Rejection.Forbidden,
            RemoteCommandGuard.Evaluate(Command(client: "other"), Target));
    }

    [Fact]
    public void RejectsStaleGenerationAndOfflineTarget()
    {
        Assert.Equal(RemoteCommandGuard.Rejection.StaleSession,
            RemoteCommandGuard.Evaluate(Command(generation: "generation-1"), Target));
        Assert.Equal(RemoteCommandGuard.Rejection.StaleSession,
            RemoteCommandGuard.Evaluate(Command(), Target with { Online = false }));
    }

    [Fact]
    public void RejectsUnsupportedCapabilitiesAndUnknownActions()
    {
        Assert.Equal(RemoteCommandGuard.Rejection.Unsupported,
            RemoteCommandGuard.Evaluate(Command("volume"), Target));
        Assert.Equal(RemoteCommandGuard.Rejection.Unsupported,
            RemoteCommandGuard.Evaluate(Command("arbitrary"), Target));
    }

    [Fact]
    public void RejectsMissingIdentityAndCommandId()
    {
        Assert.Equal(RemoteCommandGuard.Rejection.Invalid,
            RemoteCommandGuard.Evaluate(Command(commandId: ""), Target));
        Assert.Equal(RemoteCommandGuard.Rejection.Invalid,
            RemoteCommandGuard.Evaluate(Command(owner: ""), Target));
        Assert.Equal(RemoteCommandGuard.Rejection.Invalid,
            RemoteCommandGuard.Evaluate(null, Target));
    }
}
