using System;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>
/// Optional fields that can be included in playback event responses.
/// </summary>
[Flags]
public enum IncludeFlags
{
    None = 0,
    Waveform = 1,
    Sidecar = 2,
    Analysis = 4,
}
