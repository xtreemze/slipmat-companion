using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

public sealed record RcloneRemoteDescriptor(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("source")] string? Source);

public sealed record CloudRemoteListResponse(
    [property: JsonPropertyName("rcloneVersion")] string? RcloneVersion,
    [property: JsonPropertyName("remotes")] IReadOnlyList<RcloneRemoteDescriptor> Remotes);

public sealed record CloudBrowserEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("isDirectory")] bool IsDirectory,
    [property: JsonPropertyName("size")] long Size);

public sealed record CloudBrowseResponse(
    [property: JsonPropertyName("remote")] string Remote,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("entries")] IReadOnlyList<CloudBrowserEntry> Entries,
    [property: JsonPropertyName("truncated")] bool Truncated);

public sealed record CloudCreateDirectoryRequest(
    [property: JsonPropertyName("remote")] string Remote,
    [property: JsonPropertyName("path")] string Path);

public sealed record CloudProjectionStatusResponse(
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("health")] string Health,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("rcloneVersion")] string? RcloneVersion,
    [property: JsonPropertyName("remoteCount")] int RemoteCount,
    [property: JsonPropertyName("selectedRemote")] string? SelectedRemote,
    [property: JsonPropertyName("selectedRemoteType")] string? SelectedRemoteType,
    [property: JsonPropertyName("remotePath")] string? RemotePath,
    [property: JsonPropertyName("projectionPath")] string? ProjectionPath,
    [property: JsonPropertyName("projectionPathManaged")] bool ProjectionPathManaged,
    [property: JsonPropertyName("syncInProgress")] bool SyncInProgress,
    [property: JsonPropertyName("projectionPathReady")] bool ProjectionPathReady,
    [property: JsonPropertyName("projectionHasFiles")] bool ProjectionHasFiles,
    [property: JsonPropertyName("libraryReady")] bool LibraryReady,
    [property: JsonPropertyName("libraryName")] string? LibraryName,
    [property: JsonPropertyName("collectionType")] string? CollectionType);

public sealed record CloudProjectionReconcileResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("copyCompleted")] bool CopyCompleted,
    [property: JsonPropertyName("libraryReady")] bool LibraryReady,
    [property: JsonPropertyName("libraryScanQueued")] bool LibraryScanQueued);
