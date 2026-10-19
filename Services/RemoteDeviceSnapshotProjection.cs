using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Builds a bounded, owner-scoped device directory from trusted presence observations.
/// Does not discover targets, claim output support, or choose a playback target.
/// </summary>
public static class RemoteDeviceSnapshotProjection
{
    private const int MaximumDevices = 256;
    private const int MaximumActions = 16;
    private static readonly HashSet<string> KnownProviders = new(StringComparer.Ordinal)
    {
        "slipmat", "jellyfin-session", "dlna"
    };
    private static readonly HashSet<string> KnownActions = new(StringComparer.Ordinal)
    {
        "play", "pause", "stop", "seek", "next", "previous", "volume", "mute"
    };

    public static RemoteDeviceSnapshot Create(
        string authenticatedOwnerId,
        IEnumerable<RemoteDeviceObservation> observations,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedOwnerId);
        ArgumentNullException.ThrowIfNull(observations);

        // Entries from a different principal must never reach the response.
        // Stable sorting makes truncation deterministic.
        var candidates = observations
            .Where(o => o is not null &&
                string.Equals(o.OwnerId, authenticatedOwnerId, StringComparison.Ordinal) &&
                KnownProviders.Contains(o.Provider) &&
                IsValidIdentifier(o.DeviceId) &&
                IsValidIdentifier(o.SessionGeneration) &&
                o.LastObservedAt <= now &&
                o.ExpiresAt > now &&
                o.ExpiresAt <= now.AddMinutes(2) &&
                !string.IsNullOrWhiteSpace(o.DisplayName))
            .OrderBy(o => o.Provider, StringComparer.Ordinal)
            .ThenBy(o => o.DeviceId, StringComparer.Ordinal)
            .ThenByDescending(o => o.LastObservedAt)
            .ThenBy(o => o.SessionGeneration, StringComparer.Ordinal)
            .ToArray();

        var entries = new List<RemoteDeviceEntry>(Math.Min(MaximumDevices, candidates.Length));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var id = candidate.Provider + ":" + candidate.DeviceId;
            if (!seen.Add(id)) continue;
            if (entries.Count == MaximumDevices) break;
            var actions = (candidate.SupportedActions ?? Array.Empty<string>())
                .Where(action => action is not null && KnownActions.Contains(action))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(action => action, StringComparer.Ordinal)
                .Take(MaximumActions)
                .ToArray();

            entries.Add(new RemoteDeviceEntry(
                id,
                candidate.DisplayName.Trim().Length > 128
                    ? candidate.DisplayName.Trim()[..128]
                    : candidate.DisplayName.Trim(),
                candidate.Provider,
                candidate.SessionGeneration,
                actions,
                candidate.ExpiresAt));
        }

        return new RemoteDeviceSnapshot(
            Version: 1,
            Authoritative: false,
            ObservedAt: now,
            Devices: entries,
            Truncated: candidates.Select(o => o.Provider + ":" + o.DeviceId)
                .Distinct(StringComparer.Ordinal).Skip(MaximumDevices).Any());
    }

    private static bool IsValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
