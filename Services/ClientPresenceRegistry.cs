using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Ephemeral observations about a remote client. This registry never owns playback
/// state and never authorizes a remote command. Its caller must authenticate the
/// session and derive the owner and client identifiers from trusted claims.
/// </summary>
public sealed record ClientPresence(
    Guid OwnerId,
    Guid ClientId,
    Guid SessionGeneration,
    string DisplayName,
    DateTimeOffset LastSeenUtc);

/// <summary>
/// Bounded, in-memory client presence projections. A stale generation cannot
/// replace an active one; a new generation is accepted only through a separate
/// authenticated replacement operation.
/// </summary>
public sealed class ClientPresenceRegistry
{
    private const int MaximumClients = 256;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly Dictionary<(Guid OwnerId, Guid ClientId), ClientPresence> _clients = [];
    private readonly Func<DateTimeOffset> _utcNow;

    public ClientPresenceRegistry() : this(() => DateTimeOffset.UtcNow) { }

    public ClientPresenceRegistry(Func<DateTimeOffset> utcNow)
    {
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public bool Observe(Guid ownerId, Guid clientId, Guid generation, string displayName)
        => Upsert(ownerId, clientId, generation, displayName, allowReplacement: false);

    /// <summary>
    /// Intended only for a separately authenticated session handshake.
    /// Old generation heartbeats will be rejected after this replacement.
    /// </summary>
    public bool ReplaceSession(Guid ownerId, Guid clientId, Guid generation, string displayName)
        => Upsert(ownerId, clientId, generation, displayName, allowReplacement: true);

    public bool Remove(Guid ownerId, Guid clientId, Guid generation)
    {
        lock (_gate)
        {
            var key = (ownerId, clientId);
            return _clients.TryGetValue(key, out var current)
                && current.SessionGeneration == generation
                && _clients.Remove(key);
        }
    }

    public IReadOnlyList<ClientPresence> ListForOwner(Guid ownerId)
    {
        if (ownerId == Guid.Empty) return [];
        lock (_gate)
        {
            Prune(_utcNow());
            return _clients.Values.Where(item => item.OwnerId == ownerId)
                .OrderBy(item => item.ClientId).ToArray();
        }
    }

    private bool Upsert(Guid ownerId, Guid clientId, Guid generation,
        string displayName, bool allowReplacement)
    {
        if (ownerId == Guid.Empty || clientId == Guid.Empty || generation == Guid.Empty
            || string.IsNullOrWhiteSpace(displayName) || displayName.Length > 128)
        {
            return false;
        }

        var safeName = displayName.Trim();
        var now = _utcNow();
        lock (_gate)
        {
            Prune(now);
            var key = (ownerId, clientId);
            if (_clients.TryGetValue(key, out var current))
            {
                if (current.SessionGeneration != generation && !allowReplacement)
                    return false;
            }
            else if (_clients.Count >= MaximumClients)
            {
                return false;
            }

            _clients[key] = new ClientPresence(ownerId, clientId, generation, safeName, now);
            return true;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _clients.Where(item => now - item.Value.LastSeenUtc >= Lifetime)
                     .Select(item => item.Key).ToArray())
        {
            _clients.Remove(key);
        }
    }
}
