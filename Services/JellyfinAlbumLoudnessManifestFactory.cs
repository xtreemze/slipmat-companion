using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Models;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AudioGateway.Services;

internal enum AlbumManifestBuildStatus
{
    Ready,
    CloudExcluded,
    Unsupported,
}

internal sealed record AlbumLoudnessSourceInput(
    Audio Audio,
    int StreamIndex,
    string ChannelLayout,
    int SampleRate,
    int ChannelCount,
    AnalysisSourceStamp.Stamp SourceStamp);

/// <summary>Creates the canonical cheap membership snapshot for analysis and playback validation.</summary>
internal static class JellyfinAlbumLoudnessManifestFactory
{
    internal static AlbumManifestBuildStatus TryBuild(
        MusicAlbum album,
        PluginConfiguration configuration,
        CancellationToken cancellationToken,
        out List<AlbumLoudnessSourceInput> inputs,
        out AlbumLoudnessManifestV1? manifest,
        out string? reason)
    {
        inputs = [];
        manifest = null;
        reason = null;

        foreach (var audio in album.Tracks
                     .OrderBy(track => track.ParentIndexNumber ?? int.MaxValue)
                     .ThenBy(track => track.IndexNumber ?? int.MaxValue)
                     .ThenBy(track => track.Id.ToString("N"), System.StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RuntimeSettings.IsCloudProjectionPath(configuration, audio.Path))
            {
                return AlbumManifestBuildStatus.CloudExcluded;
            }

            if (!audio.IsFileProtocol || string.IsNullOrWhiteSpace(audio.Path) || !File.Exists(audio.Path))
            {
                reason = "Album contains a missing or non-local track.";
                return AlbumManifestBuildStatus.Unsupported;
            }

            var stream = audio.GetMediaStreams()
                .Where(value => value.Type == MediaStreamType.Audio
                    && value.Index >= 0
                    && value.SampleRate is > 0
                    && value.Channels is > 0
                    && !string.IsNullOrWhiteSpace(value.ChannelLayout))
                .OrderByDescending(value => value.IsDefault)
                .ThenBy(value => value.Index)
                .FirstOrDefault();
            var stamp = AnalysisSourceStamp.Capture(audio.Path);
            if (stream is null || stamp is null)
            {
                reason = "Album track is missing valid audio layout or source metadata.";
                return AlbumManifestBuildStatus.Unsupported;
            }

            inputs.Add(new AlbumLoudnessSourceInput(
                audio,
                stream.Index,
                stream.ChannelLayout!,
                stream.SampleRate!.Value,
                stream.Channels!.Value,
                stamp));
        }

        var created = AlbumLoudnessManifestV1.TryCreate(
            inputs.Select(input => new AlbumLoudnessMemberV1(
                ItemId: input.Audio.Id.ToString("N"),
                DiscNumber: input.Audio.ParentIndexNumber,
                TrackNumber: input.Audio.IndexNumber,
                ChannelLayout: input.ChannelLayout,
                StreamIndex: input.StreamIndex,
                SampleRate: input.SampleRate,
                ChannelCount: input.ChannelCount,
                SourceLength: input.SourceStamp.Length,
                SourceLastWriteUtcTicks: input.SourceStamp.LastWriteUtcTicks,
                SourceLocatorFingerprint: FingerprintLocator(input.Audio.Path!))),
            out manifest,
            out reason);
        return created ? AlbumManifestBuildStatus.Ready : AlbumManifestBuildStatus.Unsupported;
    }

    private static string FingerprintLocator(string path)
    {
        var normalized = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
