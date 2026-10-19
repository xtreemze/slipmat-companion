using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>
/// A read-only, non-authoritative view of devices observed by an authenticated host.
/// This contract does not discover devices or authorize playback commands.
/// </summary>
public sealed record RemoteDeviceObservation(
    string OwnerId,
    string Provider,
    string DeviceId,
    string DisplayName,
    string SessionGeneration,
    DateTimeOffset LastObservedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> SupportedActions);

public sealed record RemoteDeviceEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("generation")] string Generation,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

public sealed record RemoteDeviceSnapshot(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("authoritative")] bool Authoritative,
    [property: JsonPropertyName("observedAt")] DateTimeOffset ObservedAt,
    [property: JsonPropertyName("devices")] IReadOnlyList<RemoteDeviceEntry> Devices,
    [property: JsonPropertyName("truncated")] bool Truncated);
