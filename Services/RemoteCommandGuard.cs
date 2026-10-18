using System;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Pure validation of a remote command against an authenticated, current target
/// observation. The caller MUST derive owner identity and target generation from
/// trusted session state, never from an unauthenticated command payload.
/// Does not execute commands or own transport/playback state.
/// </summary>
public static class RemoteCommandGuard
{
    public enum Rejection
    {
        None,
        Invalid,
        Forbidden,
        StaleSession,
        Unsupported,
    }

    public sealed record Target(
        string OwnerId,
        string ClientId,
        string SessionGeneration,
        bool Online,
        bool CanControlTransport,
        bool CanControlVolume);

    public sealed record Request(
        string OwnerId,
        string ClientId,
        string ExpectedGeneration,
        string CommandId,
        string Action);

    public static Rejection Evaluate(Request? request, Target? target)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.OwnerId)
            || string.IsNullOrWhiteSpace(request.ClientId)
            || string.IsNullOrWhiteSpace(request.ExpectedGeneration)
            || string.IsNullOrWhiteSpace(request.CommandId)
            || request.CommandId.Length > 128
            || string.IsNullOrWhiteSpace(request.Action))
        {
            return Rejection.Invalid;
        }

        if (target is null
            || !StringComparer.Ordinal.Equals(request.OwnerId, target.OwnerId)
            || !StringComparer.Ordinal.Equals(request.ClientId, target.ClientId))
        {
            return Rejection.Forbidden;
        }

        if (!target.Online
            || !StringComparer.Ordinal.Equals(request.ExpectedGeneration, target.SessionGeneration))
        {
            return Rejection.StaleSession;
        }

        return request.Action switch
        {
            "play" or "pause" or "stop" or "next" or "previous" or "seek"
                => target.CanControlTransport ? Rejection.None : Rejection.Unsupported,
            "volume" or "mute"
                => target.CanControlVolume ? Rejection.None : Rejection.Unsupported,
            _ => Rejection.Unsupported,
        };
    }
}
