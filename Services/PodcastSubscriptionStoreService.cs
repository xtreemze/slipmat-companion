using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// File-backed per-user replica for Slipmat podcast subscription intent.
/// This service stores and merges the client-owned portable contract; it does
/// not parse feeds, resolve playback, or create Jellyfin library items.
/// </summary>
public static partial class PodcastSubscriptionStoreService
{
    public const int ContractVersion = 1;
    public const int MaxSyncRecords = 5_000;

    private const string FeedKind = "syndication-feed";
    private const string Subscribed = "subscribed";
    private const string Unsubscribed = "unsubscribed";

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> UserLocks = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    [GeneratedRegex("^[A-Za-z0-9._:@-]{1,512}$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceIdPattern();

    [GeneratedRegex("^[A-Za-z0-9._:@-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex OriginIdPattern();

    /// <summary>Loads the current authenticated user's replica.</summary>
    public static async Task<List<PodcastSubscriptionRecordV1>> LoadAsync(
        string storeRoot,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        EnsureUserId(userId);
        var gate = UserLocks.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadUnlockedAsync(storeRoot, userId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Merges a bounded client replica into the current user's server replica
    /// and returns the complete merged document for bidirectional convergence.
    /// </summary>
    public static async Task<PodcastSubscriptionSyncResponse> MergeAsync(
        string storeRoot,
        Guid userId,
        IReadOnlyList<PodcastSubscriptionRecordV1>? incoming,
        CancellationToken cancellationToken = default)
    {
        EnsureUserId(userId);
        incoming ??= Array.Empty<PodcastSubscriptionRecordV1>();
        var bounded = incoming.Take(MaxSyncRecords).ToArray();
        var rejected = Math.Max(0, incoming.Count - bounded.Length);
        var applied = 0;
        var unchanged = 0;

        var gate = UserLocks.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadUnlockedAsync(storeRoot, userId, cancellationToken).ConfigureAwait(false);
            var byId = current.ToDictionary(record => record.SubscriptionId, StringComparer.Ordinal);

            foreach (var remote in bounded)
            {
                if (!IsValidRecord(remote))
                {
                    rejected++;
                    continue;
                }

                if (!byId.TryGetValue(remote.SubscriptionId, out var local))
                {
                    byId[remote.SubscriptionId] = remote;
                    applied++;
                    continue;
                }

                var winner = Merge(local, remote);
                if (ReferenceEquals(winner, local) || winner == local)
                {
                    unchanged++;
                    continue;
                }

                byId[remote.SubscriptionId] = winner;
                applied++;
            }

            var records = byId.Values
                .OrderBy(record => record.SubscriptionId, StringComparer.Ordinal)
                .ToList();

            if (applied > 0)
            {
                await SaveUnlockedAsync(storeRoot, userId, records, cancellationToken).ConfigureAwait(false);
            }

            return new PodcastSubscriptionSyncResponse(
                Records: records,
                Applied: applied,
                Unchanged: unchanged,
                Rejected: rejected,
                Truncated: incoming.Count > MaxSyncRecords);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Validates one portable record without trusting serialized input.</summary>
    public static bool IsValidRecord(PodcastSubscriptionRecordV1? record)
    {
        if (record is null || record.Version != ContractVersion)
        {
            return false;
        }

        if (record.Status is not Subscribed and not Unsubscribed)
        {
            return false;
        }

        if (!IsValidFeed(record.Feed) ||
            !IsValidRevision(record.CreatedRevision) ||
            !IsValidRevision(record.Revision) ||
            record.CreatedRevision.Sequence > record.Revision.Sequence)
        {
            return false;
        }

        if (!IsSafePortableLocator(record.PortableFeedLocator))
        {
            return false;
        }

        return string.Equals(
            record.SubscriptionId,
            SubscriptionId(record.Feed),
            StringComparison.Ordinal);
    }

    /// <summary>Deterministic merge matching the client subscription contract.</summary>
    public static PodcastSubscriptionRecordV1 Merge(
        PodcastSubscriptionRecordV1 left,
        PodcastSubscriptionRecordV1 right)
    {
        if (!IsValidRecord(left) || !IsValidRecord(right) ||
            !string.Equals(left.SubscriptionId, right.SubscriptionId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Podcast subscription records are invalid or incompatible.");
        }

        if (left.Revision.Sequence > right.Revision.Sequence)
        {
            return left;
        }

        if (right.Revision.Sequence > left.Revision.Sequence)
        {
            return right;
        }

        if (!string.Equals(left.Status, right.Status, StringComparison.Ordinal))
        {
            return string.Equals(left.Status, Unsubscribed, StringComparison.Ordinal) ? left : right;
        }

        var originOrder = string.Compare(
            left.Revision.OriginId,
            right.Revision.OriginId,
            StringComparison.Ordinal);
        if (originOrder != 0)
        {
            return originOrder > 0 ? left : right;
        }

        var payloadOrder = string.Compare(PayloadKey(left), PayloadKey(right), StringComparison.Ordinal);
        return payloadOrder >= 0 ? left : right;
    }

    private static bool IsValidFeed(PodcastSyndicationFeedRefV1? feed)
        => feed is not null &&
           feed.Version == ContractVersion &&
           string.Equals(feed.Kind, FeedKind, StringComparison.Ordinal) &&
           feed.Resource is not null &&
           feed.Resource.Version == ContractVersion &&
           !string.IsNullOrWhiteSpace(feed.Resource.ProviderId) &&
           ResourceIdPattern().IsMatch(feed.Resource.ResourceId ?? string.Empty);

    private static bool IsValidRevision(PodcastSubscriptionRevisionV1? revision)
        => revision is not null &&
           revision.Sequence > 0 &&
           OriginIdPattern().IsMatch(revision.OriginId ?? string.Empty);

    private static bool IsSafePortableLocator(string? value)
    {
        if (value is null)
        {
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        return true;
    }

    private static string SubscriptionId(PodcastSyndicationFeedRefV1 feed)
        => $"podcast-subscription:{FeedKind}:{EncodeURIComponent(feed.Resource.ProviderId)}:{EncodeURIComponent(feed.Resource.ResourceId)}";

    private static string EncodeURIComponent(string value)
        => Uri.EscapeDataString(value)
            .Replace("%21", "!", StringComparison.OrdinalIgnoreCase)
            .Replace("%27", "'", StringComparison.OrdinalIgnoreCase)
            .Replace("%28", "(", StringComparison.OrdinalIgnoreCase)
            .Replace("%29", ")", StringComparison.OrdinalIgnoreCase)
            .Replace("%2A", "*", StringComparison.OrdinalIgnoreCase);

    private static string PayloadKey(PodcastSubscriptionRecordV1 record)
        => string.Concat(
            record.Status,
            "\0",
            record.Revision.OriginId,
            "\0",
            record.PortableFeedLocator ?? string.Empty,
            "\0",
            record.CreatedRevision.OriginId,
            "\0",
            record.CreatedRevision.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string SubscriptionPath(string storeRoot, Guid userId)
    {
        if (string.IsNullOrWhiteSpace(storeRoot))
        {
            throw new ArgumentException("Store root is required.", nameof(storeRoot));
        }

        var root = Path.GetFullPath(storeRoot);
        var directory = Path.Combine(root, "podcasts", "subscriptions");
        var path = Path.Combine(directory, $"{userId:N}.json");
        if (!StorePaths.IsWithinRoot(root, path))
        {
            throw new InvalidOperationException("Podcast subscription path escaped the configured store root.");
        }

        return path;
    }

    private static async Task<List<PodcastSubscriptionRecordV1>> LoadUnlockedAsync(
        string storeRoot,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var path = SubscriptionPath(storeRoot, userId);
        if (!File.Exists(path))
        {
            return new List<PodcastSubscriptionRecordV1>();
        }

        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<PodcastSubscriptionDocumentV1>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (document is null || document.Version != ContractVersion || document.Records is null)
        {
            throw new InvalidDataException("Podcast subscription store is malformed or incompatible.");
        }

        var records = new List<PodcastSubscriptionRecordV1>(document.Records.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in document.Records)
        {
            if (!IsValidRecord(record) || !seen.Add(record.SubscriptionId))
            {
                throw new InvalidDataException("Podcast subscription store contains invalid or duplicate records.");
            }

            records.Add(record);
        }

        return records;
    }

    private static async Task SaveUnlockedAsync(
        string storeRoot,
        Guid userId,
        List<PodcastSubscriptionRecordV1> records,
        CancellationToken cancellationToken)
    {
        var path = SubscriptionPath(storeRoot, userId);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Podcast subscription directory could not be resolved.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{userId:N}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new PodcastSubscriptionDocumentV1(ContractVersion, records),
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void EnsureUserId(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("Authenticated user id is required.", nameof(userId));
        }
    }
}
