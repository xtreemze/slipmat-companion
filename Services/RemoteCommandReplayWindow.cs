using System;
using System.Collections.Generic;
namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>Bounded process-local replay guard. Invoke after authentication and session validation.</summary>
public sealed class RemoteCommandReplayWindow
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Owner, string Client, string Generation, string Command), DateTimeOffset> _seen = [];
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _clock;

    public RemoteCommandReplayWindow(int capacity = 4096, TimeSpan? ttl = null, Func<DateTimeOffset>? clock = null)
    {
        if (capacity < 1 || capacity > 65536) throw new ArgumentOutOfRangeException(nameof(capacity));
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
        if (_ttl <= TimeSpan.Zero || _ttl > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(ttl));
        _capacity = capacity;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Admit once within the window. Fail closed when full; this is not persistent idempotency.</summary>
    public bool TryAdmit(string owner, string client, string generation, string command)
    {
        if (!Valid(owner) || !Valid(client) || !Valid(generation) || !Valid(command)) return false;
        lock (_gate)
        {
            var now = _clock();
            var expired = new List<(string, string, string, string)>();
            foreach (var pair in _seen)
                if (pair.Value <= now) expired.Add(pair.Key);
            foreach (var key in expired) _seen.Remove(key);
            var token = (owner, client, generation, command);
            if (_seen.ContainsKey(token) || _seen.Count >= _capacity) return false;
            _seen.Add(token, now + _ttl);
            return true;
        }
    }

    private static bool Valid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.AsSpan().IndexOfAny('\r', '\n', '\0') < 0;
}
