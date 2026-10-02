using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>
/// Sanitized administrator-facing status for an operator-authenticated Jottacloud daemon projection.
/// </summary>
public sealed record JottacloudProjectionStatusResponse(
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("health")] string Health,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("cliVersion")] string? CliVersion,
    [property: JsonPropertyName("downloadQueueKnown")] bool DownloadQueueKnown,
    [property: JsonPropertyName("downloadQueueEntries")] int DownloadQueueEntries,
    [property: JsonPropertyName("projectionPathReady")] bool ProjectionPathReady,
    [property: JsonPropertyName("projectionHasFiles")] bool ProjectionHasFiles,
    [property: JsonPropertyName("libraryReady")] bool LibraryReady,
    [property: JsonPropertyName("libraryName")] string? LibraryName,
    [property: JsonPropertyName("collectionType")] string? CollectionType);

/// <summary>
/// Read-only remote directory projection returned to an elevated administrator.
/// </summary>
public sealed record JottacloudBrowseResponse(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("lines")] IReadOnlyList<string> Lines,
    [property: JsonPropertyName("truncated")] bool Truncated);

/// <summary>
/// Result of one bounded projection reconciliation attempt.
/// </summary>
public sealed record JottacloudProjectionReconcileResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("downloadQueued")] bool DownloadQueued,
    [property: JsonPropertyName("libraryReady")] bool LibraryReady,
    [property: JsonPropertyName("libraryScanQueued")] bool LibraryScanQueued);
