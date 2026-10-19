using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>Non-authoritative, read-only projection scoped to the authenticated Jellyfin user.</summary>
public static class RemoteSessionProjection
{
    /// <summary>Maximum sessions returned by one user-scoped snapshot.</summary>
    public const int MaximumSessions = 128;

    /// <summary>Maximum accepted age of a session observation.</summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(45);

    /// <summary>Server-observed session facts before user-scoped projection.</summary>
    /// <param name="SessionId">Opaque Jellyfin session identifier.</param>
    /// <param name="DeviceId">Internal device identifier; never included in the response.</param>
    /// <param name="DisplayName">Untrusted display name, sanitized before projection.</param>
    /// <param name="Active">Whether Jellyfin currently considers the session active.</param>
    /// <param name="SupportsRemoteControl">Whether the session advertises remote-control support.</param>
    /// <param name="ObservedAt">Server-reported last activity time.</param>
    /// <param name="OwnerUserId">Jellyfin owner, used only to enforce caller scope.</param>
    public sealed record Observation(
        string SessionId,
        string? DeviceId,
        string? DisplayName,
        bool Active,
        bool SupportsRemoteControl,
        DateTimeOffset ObservedAt,
        Guid OwnerUserId);

    /// <summary>A bounded public projection of one controllable Jellyfin session.</summary>
    /// <param name="Id">Namespaced session identity.</param>
    /// <param name="Name">Sanitized device label.</param>
    /// <param name="Kind">The provider namespace for this session.</param>
    /// <param name="Commands">Baseline Jellyfin transport actions for remote-capable sessions.</param>
    /// <param name="ObservedAt">The source session's last activity time.</param>
    public sealed record Session(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("commands")] IReadOnlyList<string> Commands,
        [property: JsonPropertyName("observedAt")] DateTimeOffset ObservedAt);

    /// <summary>A versioned, non-authoritative snapshot of the authenticated user's sessions.</summary>
    /// <param name="SchemaVersion">Snapshot contract version.</param>
    /// <param name="Authoritative">Always false; playback remains target-owned.</param>
    /// <param name="Sessions">Fresh, active, remote-capable sessions within the result bound.</param>
    public sealed record Snapshot(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("authoritative")] bool Authoritative,
        [property: JsonPropertyName("sessions")] IReadOnlyList<Session> Sessions);

    private static readonly string[] TransportCommands =
        ["play", "pause", "stop", "seek", "next", "previous"];

    /// <summary>Projects only fresh remote-capable sessions belonging to the authenticated user.</summary>
    /// <param name="observations">Server-observed session facts.</param>
    /// <param name="authenticatedUserId">User id from the authenticated Jellyfin claim.</param>
    /// <param name="now">Current server time used for freshness validation.</param>
    /// <returns>A bounded non-authoritative snapshot.</returns>
    public static Snapshot ProjectForUser(
        IEnumerable<Observation> observations,
        Guid authenticatedUserId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (authenticatedUserId == Guid.Empty)
            return new Snapshot(1, false, Array.Empty<Session>());

        // Session identity is exposed only after ownership, liveness, capability,
        // and freshness checks. The owner identifier never enters the projection.
        var selected = new Dictionary<string, Session>(StringComparer.Ordinal);
        foreach (var observation in observations.Take(MaximumSessions + 1))
        {
            if (selected.Count >= MaximumSessions)
                break;
            if (observation.OwnerUserId != authenticatedUserId ||
                !observation.Active || !observation.SupportsRemoteControl ||
                !ValidId(observation.SessionId) ||
                observation.ObservedAt > now ||
                now - observation.ObservedAt > MaximumAge)
                continue;

            var id = "jellyfin-session:" + observation.SessionId;
            var name = SanitizeName(observation.DisplayName);
            var session = new Session(id, name, "jellyfin-session",
                TransportCommands, observation.ObservedAt);
            if (!selected.TryGetValue(id, out var existing) || existing.ObservedAt < session.ObservedAt)
                selected[id] = session;
        }

        return new Snapshot(1, false,
            selected.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray());
    }

    private static bool ValidId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(c => (c >= 'a' && c <= 'z') ||
                       (c >= 'A' && c <= 'Z') ||
                       (c >= '0' && c <= '9') || c is '-' or '_');

    private static string SanitizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Remote session";
        return new string(value.Where(c => !char.IsControl(c)).Take(96).ToArray()).Trim() is { Length: > 0 } name
            ? name
            : "Remote session";
    }
}
