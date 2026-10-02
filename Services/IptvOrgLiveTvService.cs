using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Optional Jellyfin Live TV projection of the server-admin-selected iptv-org
/// channels. The selection is server source configuration only; Slipmat's
/// client-owned per-user Live TV subscriptions remain authoritative for the
/// client library.
/// </summary>
public sealed class IptvOrgLiveTvService : ILiveTvService
{
    private const string ChannelPrefix = "slipmat_iptv_org_";
    private readonly IptvOrgCatalogService _catalog;
    private readonly Func<IReadOnlySet<string>> _selectedChannelIds;

    public IptvOrgLiveTvService(IptvOrgCatalogService catalog)
        : this(catalog, SelectedChannelIds)
    {
    }

    /// <summary>
    /// Creates a projection with an explicit selection reader. Production uses
    /// plugin configuration; tests can supply deterministic server source state.
    /// </summary>
    public IptvOrgLiveTvService(
        IptvOrgCatalogService catalog,
        Func<IReadOnlySet<string>> selectedChannelIds)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _selectedChannelIds = selectedChannelIds ?? throw new ArgumentNullException(nameof(selectedChannelIds));
    }

    public string Name => "Slipmat iptv-org";

    public string HomePageUrl => "https://iptv-org.github.io/";

    public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        var catalog = await _catalog.GetAsync(cancellationToken).ConfigureAwait(false);
        var selected = _selectedChannelIds();
        return catalog.Channels
            .Where(channel => selected.Contains(channel.Id))
            .Select(ProjectChannel)
            .ToArray();
    }

    public async Task<MediaSourceInfo> GetChannelStream(
        string channelId,
        string streamId,
        CancellationToken cancellationToken)
    {
        var sources = await GetChannelStreamMediaSources(channelId, cancellationToken)
            .ConfigureAwait(false);
        if (sources.Count == 0)
        {
            throw new InvalidOperationException("No playable iptv-org stream is available for this channel.");
        }

        if (!string.IsNullOrWhiteSpace(streamId))
        {
            var selected = sources.FirstOrDefault(source =>
                source.Id.Equals(streamId, StringComparison.Ordinal));
            if (selected is not null)
            {
                return selected;
            }
        }

        return sources[0];
    }

    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(
        string channelId,
        CancellationToken cancellationToken)
    {
        var channel = await ResolveSelectedChannelAsync(channelId, cancellationToken)
            .ConfigureAwait(false);
        if (channel is null)
        {
            return new List<MediaSourceInfo>();
        }

        return channel.Streams
            .Select(stream => ProjectSource(channel, stream))
            .ToList();
    }

    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        string channelId,
        DateTime startDateUtc,
        DateTime endDateUtc,
        CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<ProgramInfo>>(Array.Empty<ProgramInfo>());

    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<TimerInfo>>(Array.Empty<TimerInfo>());

    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<SeriesTimerInfo>>(Array.Empty<SeriesTimerInfo>());

    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(
        CancellationToken cancellationToken,
        ProgramInfo? program = null)
        => Task.FromResult(new SeriesTimerInfo());

    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
        => UnsupportedRecordingOperation();

    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
        => UnsupportedRecordingOperation();

    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        => UnsupportedRecordingOperation();

    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => UnsupportedRecordingOperation();

    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
        => UnsupportedRecordingOperation();

    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => UnsupportedRecordingOperation();

    public Task CloseLiveStream(string id, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task ResetTuner(string id, CancellationToken cancellationToken)
        => Task.CompletedTask;

    private async Task<IptvOrgCatalogChannel?> ResolveSelectedChannelAsync(
        string jellyfinChannelId,
        CancellationToken cancellationToken)
    {
        var selected = _selectedChannelIds();
        if (selected.Count == 0)
        {
            return null;
        }

        var catalog = await _catalog.GetAsync(cancellationToken).ConfigureAwait(false);
        return catalog.Channels.FirstOrDefault(channel =>
            selected.Contains(channel.Id) &&
            MakeChannelId(channel.Id).Equals(jellyfinChannelId, StringComparison.Ordinal));
    }

    private static IReadOnlySet<string> SelectedChannelIds()
        => (Plugin.Instance?.Configuration.IptvOrgLiveTvChannelIds ?? Array.Empty<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .ToHashSet(StringComparer.Ordinal);

    private static ChannelInfo ProjectChannel(IptvOrgCatalogChannel channel)
        => new()
        {
            Id = MakeChannelId(channel.Id),
            TunerChannelId = channel.Id,
            Name = channel.Name,
            Number = string.Empty,
            CallSign = channel.Network,
            ChannelType = ChannelType.TV,
            ChannelGroup = channel.Categories.FirstOrDefault(),
            ImageUrl = channel.LogoUrl,
            HasImage = channel.LogoUrl is not null,
            Tags = channel.Categories
                .Concat(channel.Languages)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };

    private static MediaSourceInfo ProjectSource(
        IptvOrgCatalogChannel channel,
        IptvOrgCatalogStream stream)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (stream.Referrer is not null)
        {
            headers["Referer"] = stream.Referrer;
        }

        if (stream.UserAgent is not null)
        {
            headers["User-Agent"] = stream.UserAgent;
        }

        var uri = new Uri(stream.Url, UriKind.Absolute);
        var extension = System.IO.Path.GetExtension(uri.AbsolutePath);
        var isManifest = extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".m3u", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mpd", StringComparison.OrdinalIgnoreCase);
        var hasHeaders = headers.Count > 0;

        return new MediaSourceInfo
        {
            Id = StableId($"{channel.Id}\n{stream.FeedId}\n{stream.Url}"),
            Name = stream.Title ?? channel.Name,
            Path = stream.Url,
            Protocol = MediaProtocol.Http,
            IsRemote = true,
            IsInfiniteStream = true,
            RequiresOpening = true,
            RequiresClosing = true,
            SupportsProbing = true,
            SupportsTranscoding = true,
            SupportsDirectStream = true,
            SupportsDirectPlay = !isManifest && !hasHeaders,
            UseMostCompatibleTranscodingProfile = true,
            RequiredHttpHeaders = headers,
            MediaStreams =
            [
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = -1,
                },
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = -1,
                },
            ],
        };
    }

    private static string MakeChannelId(string providerChannelId)
        => ChannelPrefix + StableId(providerChannelId);

    private static string StableId(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static Task UnsupportedRecordingOperation()
        => Task.FromException(new NotSupportedException(
            "Recording control is not provided by the iptv-org discovery projection."));
}
