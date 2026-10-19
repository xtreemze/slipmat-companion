using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>Non-authoritative, read-only projection scoped to the authenticated Jellyfin user.</summary>
public static class RemoteSessionProjection
{
    public const int MaximumSessions = 128;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(45);

    public sealed record Observation(
        string SessionId,
        string? DeviceId,
        string? DisplayName,
        bool Active,
        bool SupportsRemoteControl,
        DateTimeOffset ObservedAt,
        Guid OwnerUserId);

    public sealed record Session(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("commands")] IReadOnlyList<string> Commands,
        [property: JsonPropertyName("observedAt")] DateTimeOffset ObservedAt);

    public sealed record Snapshot(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("authoritative")] bool Authoritative,
        [property: JsonPropertyName("sessions")] IReadOnlyList<Session> Sessions);

    private static readonly string[] TransportCommands =
        ["play", "pause", "stop", "seek", "next", "previous"];

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
