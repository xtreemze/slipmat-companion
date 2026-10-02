using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Server-side iptv-org catalog reader used only by the companion dashboard and
/// Jellyfin Live TV projection. It reuses the companion's fixed-host, bounded
/// directory transport and keeps a last-known-good normalized snapshot.
/// </summary>
public sealed class IptvOrgCatalogService
{
    public const int ContractVersion = 1;
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    private const string Origin = "https://iptv-org.github.io/api/";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Func<CancellationToken, Task<IptvOrgCatalogSnapshot>> _loader;
    private IptvOrgCatalogSnapshot? _cached;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    /// <summary>Creates the production catalog service.</summary>
    public IptvOrgCatalogService()
        : this(LoadFreshAsync)
    {
    }

    /// <summary>
    /// Creates a catalog service with an explicit loader. This seam keeps network
    /// acquisition outside deterministic Live TV projection tests.
    /// </summary>
    public IptvOrgCatalogService(
        Func<CancellationToken, Task<IptvOrgCatalogSnapshot>> loader)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    }

    /// <summary>Gets the current normalized catalog, refreshing when its TTL expires.</summary>
    public async Task<IptvOrgCatalogSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var cached = _cached;
        if (cached is not null && DateTimeOffset.UtcNow - _cachedAt <= CacheDuration)
        {
            return cached;
        }

        var refreshed = await RefreshCoreAsync(force: false, cancellationToken).ConfigureAwait(false);
        return refreshed.Snapshot;
    }

    /// <summary>
    /// Forces one upstream refresh. If a previous snapshot exists, a transient
    /// provider failure retains it and reports that last-known-good evidence was used.
    /// </summary>
    public Task<IptvOrgCatalogRefreshResult> RefreshAsync(CancellationToken cancellationToken)
        => RefreshCoreAsync(force: true, cancellationToken);

    /// <summary>Returns one channel by provider-local ID.</summary>
    public async Task<IptvOrgCatalogChannel?> GetChannelAsync(
        string channelId,
        CancellationToken cancellationToken)
    {
        var catalog = await GetAsync(cancellationToken).ConfigureAwait(false);
        return catalog.ById.TryGetValue(channelId, out var channel) ? channel : null;
    }

    private async Task<IptvOrgCatalogRefreshResult> RefreshCoreAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cached = _cached;
            if (!force &&
                cached is not null &&
                DateTimeOffset.UtcNow - _cachedAt <= CacheDuration)
            {
                return new IptvOrgCatalogRefreshResult(cached, UsedLastKnownGood: false);
            }

            try
            {
                var refreshed = await _loader(cancellationToken).ConfigureAwait(false);
                _cached = refreshed;
                _cachedAt = DateTimeOffset.UtcNow;
                return new IptvOrgCatalogRefreshResult(refreshed, UsedLastKnownGood: false);
            }
            catch when (cached is not null)
            {
                // Keep serving the prior snapshot and back off automatic refresh attempts
                // for one cache interval. Explicit RefreshAsync calls still force a retry.
                _cachedAt = DateTimeOffset.UtcNow;
                return new IptvOrgCatalogRefreshResult(cached, UsedLastKnownGood: true);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static async Task<IptvOrgCatalogSnapshot> LoadFreshAsync(CancellationToken cancellationToken)
    {
        var channelsTask = FetchArrayAsync<ChannelDto>("channels.json", cancellationToken);
        var feedsTask = FetchArrayAsync<FeedDto>("feeds.json", cancellationToken);
        var logosTask = FetchArrayAsync<LogoDto>("logos.json", cancellationToken);
        var streamsTask = FetchArrayAsync<StreamDto>("streams.json", cancellationToken);
        var guidesTask = FetchArrayAsync<GuideDto>("guides.json", cancellationToken);

        await Task.WhenAll(channelsTask, feedsTask, logosTask, streamsTask, guidesTask)
            .ConfigureAwait(false);

        return Normalize(
            await channelsTask.ConfigureAwait(false),
            await feedsTask.ConfigureAwait(false),
            await logosTask.ConfigureAwait(false),
            await streamsTask.ConfigureAwait(false),
            await guidesTask.ConfigureAwait(false),
            DateTimeOffset.UtcNow);
    }

    internal static IptvOrgCatalogSnapshot Normalize(
        IReadOnlyList<ChannelDto> channels,
        IReadOnlyList<FeedDto> feeds,
        IReadOnlyList<LogoDto> logos,
        IReadOnlyList<StreamDto> streams,
        IReadOnlyList<GuideDto> guides,
        DateTimeOffset observedAt)
    {
        var feedByChannel = feeds
            .Where(static feed => Text(feed.Channel) is not null && Text(feed.Id) is not null)
            .GroupBy(feed => feed.Channel!.Trim(), StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.Ordinal);

        var mainFeedIds = feeds
            .Where(static feed => feed.IsMain && Text(feed.Channel) is not null && Text(feed.Id) is not null)
            .Select(static feed => $"{feed.Channel!.Trim()}\n{feed.Id!.Trim()}")
            .ToHashSet(StringComparer.Ordinal);

        var logoByChannel = logos
            .Where(static logo =>
                Text(logo.Channel) is not null &&
                SafeHttpUrl(logo.Url) is not null &&
                logo.Width > 0 &&
                logo.Height > 0)
            .Select(static logo => new IptvOrgCatalogLogo(
                ChannelId: logo.Channel!.Trim(),
                FeedId: Text(logo.Feed),
                Url: SafeHttpUrl(logo.Url)!,
                Width: logo.Width,
                Height: logo.Height,
                Format: Text(logo.Format),
                Tags: TextList(logo.Tags),
                InUse: logo.InUse))
            .GroupBy(static logo => logo.ChannelId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<IptvOrgCatalogLogo>)group.ToArray(),
                StringComparer.Ordinal);

        var streamByChannel = streams
            .Where(static stream => Text(stream.Channel) is not null && SafeHttpUrl(stream.Url) is not null)
            .Select(stream => new IptvOrgCatalogStream(
                ChannelId: stream.Channel!.Trim(),
                FeedId: Text(stream.Feed),
                Title: Text(stream.Title),
                Url: SafeHttpUrl(stream.Url)!,
                Referrer: SafeHttpUrl(stream.Referrer),
                UserAgent: SafeHeaderValue(stream.UserAgent),
                Quality: Text(stream.Quality),
                IsMainFeed: Text(stream.Feed) is string feedId &&
                    mainFeedIds.Contains($"{stream.Channel!.Trim()}\n{feedId}")))
            .GroupBy(static stream => stream.ChannelId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<IptvOrgCatalogStream>)group.ToArray(),
                StringComparer.Ordinal);

        var guideByChannel = guides
            .Where(static guide => Text(guide.Channel) is not null && guide.Sources is not null)
            .SelectMany(static guide =>
            {
                var channelId = guide.Channel!.Trim();
                var feedId = Text(guide.Feed);
                return guide.Sources!
                    .Where(static source =>
                        SafeHttpUrl(source.Url) is not null &&
                        (Text(source.Format) is null ||
                            Text(source.Format)!.Equals("XML", StringComparison.OrdinalIgnoreCase)))
                    .Select(source => new IptvOrgCatalogGuideSource(
                        ChannelId: channelId,
                        FeedId: feedId,
                        Url: SafeHttpUrl(source.Url)!,
                        Format: Text(source.Format)));
            })
            .GroupBy(static source => source.ChannelId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<IptvOrgCatalogGuideSource>)group.ToArray(),
                StringComparer.Ordinal);

        var normalized = new List<IptvOrgCatalogChannel>();
        foreach (var channel in channels)
        {
            var id = Text(channel.Id);
            var name = Text(channel.Name);
            var country = Text(channel.Country)?.ToUpperInvariant();
            if (id is null ||
                name is null ||
                country is null ||
                channel.IsNsfw ||
                Text(channel.Closed) is not null ||
                Text(channel.ReplacedBy) is not null)
            {
                continue;
            }

            var channelFeeds = feedByChannel.TryGetValue(id, out var feedCandidates)
                ? feedCandidates
                : Array.Empty<FeedDto>();
            var feedIds = channelFeeds
                .Select(static feed => Text(feed.Id))
                .Where(static value => value is not null)
                .Select(static value => value!)
                .ToHashSet(StringComparer.Ordinal);

            var channelStreams = streamByChannel.TryGetValue(id, out var streamCandidates)
                ? streamCandidates
                    .Where(stream => IsFeedReferenceAllowed(stream.FeedId, feedIds))
                    .OrderByDescending(static stream => stream.IsMainFeed)
                    .ThenByDescending(static stream => QualityRank(stream.Quality))
                    .ThenBy(static stream => stream.Url, StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<IptvOrgCatalogStream>();
            if (channelStreams.Length == 0)
            {
                continue;
            }

            var channelLogos = logoByChannel.TryGetValue(id, out var logoCandidates)
                ? logoCandidates
                    .Where(logo => IsFeedReferenceAllowed(logo.FeedId, feedIds))
                    .OrderByDescending(static logo => logo.InUse)
                    .ThenByDescending(static logo => (long)logo.Width * logo.Height)
                    .ThenBy(static logo => logo.Url, StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<IptvOrgCatalogLogo>();

            var channelGuides = guideByChannel.TryGetValue(id, out var guideCandidates)
                ? guideCandidates
                    .Where(guide => IsFeedReferenceAllowed(guide.FeedId, feedIds))
                    .GroupBy(static guide => guide.Url, StringComparer.Ordinal)
                    .Select(static group => group.First())
                    .OrderBy(static guide => guide.Url, StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<IptvOrgCatalogGuideSource>();

            var languages = channelFeeds
                .SelectMany(static feed => TextList(feed.Languages))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            normalized.Add(new IptvOrgCatalogChannel(
                Id: id,
                Name: name,
                AlternateNames: TextList(channel.AltNames),
                CountryCode: country,
                Network: Text(channel.Network),
                Owners: TextList(channel.Owners),
                Categories: TextList(channel.Categories),
                Languages: languages,
                LogoUrl: channelLogos.FirstOrDefault()?.Url,
                Logos: channelLogos,
                Streams: channelStreams,
                GuideSources: channelGuides,
                HasGuide: channelGuides.Length > 0));
        }

        var ordered = normalized
            .OrderBy(static channel => channel.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static channel => channel.Id, StringComparer.Ordinal)
            .ToArray();
        var guideUrls = ordered
            .SelectMany(static channel => channel.GuideSources)
            .Select(static guide => guide.Url)
            .ToHashSet(StringComparer.Ordinal);

        return new IptvOrgCatalogSnapshot(
            ObservedAt: observedAt,
            Channels: ordered,
            ById: ordered.ToDictionary(static channel => channel.Id, StringComparer.Ordinal),
            GuideSourceUrls: guideUrls);
    }

    private static async Task<IReadOnlyList<T>> FetchArrayAsync<T>(
        string resource,
        CancellationToken cancellationToken)
    {
        var response = await DirectoryResourceFetchService.FetchAsync(
                new DirectoryResourceFetchRequest(
                    Version: DirectoryResourceFetchService.ContractVersion,
                    ProviderId: DirectoryResourceFetchService.IptvOrgProviderId,
                    Url: Origin + resource),
                cancellationToken)
            .ConfigureAwait(false);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(response.BodyBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("iptv-org directory payload was not valid base64.", ex);
        }

        var result = JsonSerializer.Deserialize<List<T>>(bytes, JsonOptions);
        return result ?? throw new InvalidOperationException("iptv-org directory payload was not a JSON array.");
    }

    /// <summary>
    /// Applies the iptv-org cross-reference rule used for streams, logos, and guides:
    /// unscoped evidence is allowed; scoped evidence must match a declared feed when
    /// the channel has declared feeds.
    /// </summary>
    public static bool IsFeedReferenceAllowed(
        string? feedId,
        IReadOnlySet<string> knownFeedIds)
        => feedId is null || knownFeedIds.Count == 0 || knownFeedIds.Contains(feedId);

    private static string? Text(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static IReadOnlyList<string> TextList(IEnumerable<string?>? values)
        => values is null
            ? Array.Empty<string>()
            : values
                .Select(Text)
                .Where(static value => value is not null)
                .Select(static value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static string? SafeHeaderValue(string? value)
    {
        var candidate = Text(value);
        if (candidate is null ||
            candidate.Length > 512 ||
            candidate.Any(static value => char.IsControl(value)))
        {
            return null;
        }

        return candidate;
    }

    private static string? SafeHttpUrl(string? value)
    {
        var candidate = Text(value);
        if (candidate is null ||
            !PodcastResourceFetchService.TryValidateTargetUri(candidate, out var parsed) ||
            parsed is null)
        {
            return null;
        }

        return parsed.AbsoluteUri;
    }

    private static int QualityRank(string? quality)
    {
        if (quality is null)
        {
            return 0;
        }

        var digits = new string(quality.TakeWhile(static value => char.IsDigit(value)).ToArray());
        return int.TryParse(digits, out var pixels) ? pixels : 0;
    }

    internal sealed class ChannelDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("alt_names")]
        public string?[]? AltNames { get; init; }

        [JsonPropertyName("network")]
        public string? Network { get; init; }

        [JsonPropertyName("owners")]
        public string?[]? Owners { get; init; }

        [JsonPropertyName("country")]
        public string? Country { get; init; }

        [JsonPropertyName("categories")]
        public string?[]? Categories { get; init; }

        [JsonPropertyName("is_nsfw")]
        public bool IsNsfw { get; init; }

        [JsonPropertyName("closed")]
        public string? Closed { get; init; }

        [JsonPropertyName("replaced_by")]
        public string? ReplacedBy { get; init; }
    }

    internal sealed class FeedDto
    {
        [JsonPropertyName("channel")]
        public string? Channel { get; init; }

        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("is_main")]
        public bool IsMain { get; init; }

        [JsonPropertyName("languages")]
        public string?[]? Languages { get; init; }
    }

    internal sealed class LogoDto
    {
        [JsonPropertyName("channel")]
        public string? Channel { get; init; }

        [JsonPropertyName("feed")]
        public string? Feed { get; init; }

        [JsonPropertyName("in_use")]
        public bool InUse { get; init; }

        [JsonPropertyName("width")]
        public int Width { get; init; }

        [JsonPropertyName("height")]
        public int Height { get; init; }

        [JsonPropertyName("format")]
        public string? Format { get; init; }

        [JsonPropertyName("tags")]
        public string?[]? Tags { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }
    }

    internal sealed class StreamDto
    {
        [JsonPropertyName("channel")]
        public string? Channel { get; init; }

        [JsonPropertyName("feed")]
        public string? Feed { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("referrer")]
        public string? Referrer { get; init; }

        [JsonPropertyName("user_agent")]
        public string? UserAgent { get; init; }

        [JsonPropertyName("quality")]
        public string? Quality { get; init; }
    }

    internal sealed class GuideDto
    {
        [JsonPropertyName("channel")]
        public string? Channel { get; init; }

        [JsonPropertyName("feed")]
        public string? Feed { get; init; }

        [JsonPropertyName("sources")]
        public GuideSourceDto[]? Sources { get; init; }
    }

    internal sealed class GuideSourceDto
    {
        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("format")]
        public string? Format { get; init; }
    }
}

public sealed record IptvOrgCatalogRefreshResult(
    IptvOrgCatalogSnapshot Snapshot,
    bool UsedLastKnownGood);

public sealed record IptvOrgCatalogSnapshot(
    DateTimeOffset ObservedAt,
    IReadOnlyList<IptvOrgCatalogChannel> Channels,
    IReadOnlyDictionary<string, IptvOrgCatalogChannel> ById,
    IReadOnlySet<string> GuideSourceUrls);

public sealed record IptvOrgCatalogChannel(
    string Id,
    string Name,
    IReadOnlyList<string> AlternateNames,
    string CountryCode,
    string? Network,
    IReadOnlyList<string> Owners,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Languages,
    string? LogoUrl,
    IReadOnlyList<IptvOrgCatalogLogo> Logos,
    IReadOnlyList<IptvOrgCatalogStream> Streams,
    IReadOnlyList<IptvOrgCatalogGuideSource> GuideSources,
    bool HasGuide);

public sealed record IptvOrgCatalogLogo(
    string ChannelId,
    string? FeedId,
    string Url,
    int Width,
    int Height,
    string? Format,
    IReadOnlyList<string> Tags,
    bool InUse);

public sealed record IptvOrgCatalogStream(
    string ChannelId,
    string? FeedId,
    string? Title,
    string Url,
    string? Referrer,
    string? UserAgent,
    string? Quality,
    bool IsMainFeed);

public sealed record IptvOrgCatalogGuideSource(
    string ChannelId,
    string? FeedId,
    string Url,
    string? Format);
