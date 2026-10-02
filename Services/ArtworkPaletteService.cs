using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.AudioGateway.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Extracts a small, bounded set of representative RGB swatches from Jellyfin-owned
/// primary artwork. Results are cached by an opaque artwork revision so track/batch
/// metadata can carry colour evidence before playback without browser image decoding.
/// </summary>
public sealed class ArtworkPaletteService
{
    private const int MaxSamples = 4096;
    private const int MaxSwatches = 6;
    private const int MaxCacheEntries = 512;

    private readonly JellyfinMetadataAdapter _metadataAdapter;
    private readonly ILogger<ArtworkPaletteService> _logger;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, ArtworkPaletteV1> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();

    public ArtworkPaletteService(
        JellyfinMetadataAdapter metadataAdapter,
        ILogger<ArtworkPaletteService> logger)
    {
        _metadataAdapter = metadataAdapter;
        _logger = logger;
    }

    /// <summary>
    /// Resolves album-primary artwork first, then track-primary artwork, matching the
    /// metadata adapter's existing artwork association policy.
    /// </summary>
    public ArtworkPaletteV1? ResolveForTrack(Audio track)
    {
        var source = _metadataAdapter.ResolvePrimaryArtworkSource(track);
        if (source is null)
        {
            return null;
        }

        var sourceKind = source.Id == track.Id ? "track-primary" : "album-primary";
        return Resolve(source, sourceKind);
    }

    private ArtworkPaletteV1? Resolve(BaseItem source, string sourceKind)
    {
        var image = source.GetImageInfo(ImageType.Primary, 0);
        var path = image?.Path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(path);
            var revision = BuildRevision(source.Id, image!.DateModified, file.Length);
            var cacheKey = $"{source.Id:N}:{revision}";

            lock (_cacheGate)
            {
                if (_cache.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }
            }

            using var bitmap = SKBitmap.Decode(path);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return null;
            }

            var swatches = ExtractSwatches(SamplePixels(bitmap));
            if (swatches.Count == 0)
            {
                return null;
            }

            var palette = new ArtworkPaletteV1(
                ArtworkPaletteV1.CurrentVersion,
                sourceKind,
                source.Id.ToString(),
                revision,
                swatches);
            Remember(cacheKey, palette);
            return palette;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Artwork palette extraction unavailable for Jellyfin item {ItemId}",
                source.Id);
            return null;
        }
    }

    internal static string BuildRevision(Guid sourceItemId, DateTime imageModified, long fileLength)
    {
        var material = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{sourceItemId:N}|{imageModified.ToUniversalTime().Ticks}|{fileLength}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    internal static IReadOnlyList<ArtworkPaletteSwatchV1> ExtractSwatches(
        ReadOnlySpan<byte> rgbaSamples)
    {
        if (rgbaSamples.Length == 0 || rgbaSamples.Length % 4 != 0)
        {
            return Array.Empty<ArtworkPaletteSwatchV1>();
        }

        var buckets = new Dictionary<int, PaletteBucket>();
        var totalWeight = 0d;

        for (var offset = 0; offset < rgbaSamples.Length; offset += 4)
        {
            var red = rgbaSamples[offset];
            var green = rgbaSamples[offset + 1];
            var blue = rgbaSamples[offset + 2];
            var alpha = rgbaSamples[offset + 3];
            if (alpha < 160)
            {
                continue;
            }

            var max = Math.Max(red, Math.Max(green, blue));
            var min = Math.Min(red, Math.Min(green, blue));
            var chroma = max - min;
            var luminance = (red * 299 + green * 587 + blue * 114) / 1000;
            if (luminance < 18 || luminance > 238 || chroma < 14)
            {
                continue;
            }

            var saturation = chroma / (double)Math.Max(1, max);
            var sampleWeight = 0.35d + saturation;
            var key = ((red >> 3) << 10) | ((green >> 3) << 5) | (blue >> 3);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new PaletteBucket();
                buckets.Add(key, bucket);
            }

            bucket.Red += red * sampleWeight;
            bucket.Green += green * sampleWeight;
            bucket.Blue += blue * sampleWeight;
            bucket.Weight += sampleWeight;
            totalWeight += sampleWeight;
        }

        if (buckets.Count == 0 || totalWeight <= 0d)
        {
            return Array.Empty<ArtworkPaletteSwatchV1>();
        }

        var candidates = buckets
            .Select(pair =>
            {
                var bucket = pair.Value;
                var red = (byte)Math.Clamp(
                    (int)Math.Round(bucket.Red / bucket.Weight),
                    byte.MinValue,
                    byte.MaxValue);
                var green = (byte)Math.Clamp(
                    (int)Math.Round(bucket.Green / bucket.Weight),
                    byte.MinValue,
                    byte.MaxValue);
                var blue = (byte)Math.Clamp(
                    (int)Math.Round(bucket.Blue / bucket.Weight),
                    byte.MinValue,
                    byte.MaxValue);
                return new PaletteCandidate(
                    Key: pair.Key,
                    Red: red,
                    Green: green,
                    Blue: blue,
                    Weight: bucket.Weight,
                    Hue: HueDegrees(red, green, blue));
            })
            .OrderByDescending(candidate => candidate.Weight)
            .ThenBy(candidate => candidate.Key)
            .ToList();

        var selected = new List<PaletteCandidate>(Math.Min(MaxSwatches, candidates.Count))
        {
            candidates[0],
        };

        while (selected.Count < MaxSwatches && selected.Count < candidates.Count)
        {
            PaletteCandidate? best = null;
            var bestScore = double.NegativeInfinity;
            var maxWeight = candidates[0].Weight;

            foreach (var candidate in candidates)
            {
                if (selected.Any(value => value.Key == candidate.Key))
                {
                    continue;
                }

                var hueDistance = selected.Min(value => HueDistance(value.Hue, candidate.Hue));
                var score =
                    0.45d * (candidate.Weight / maxWeight) +
                    0.55d * (hueDistance / 180d);
                if (score > bestScore ||
                    (Math.Abs(score - bestScore) < 1e-12 &&
                     (best is null || candidate.Key < best.Key)))
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            if (best is null)
            {
                break;
            }

            selected.Add(best);
        }

        return selected
            .Select(candidate => new ArtworkPaletteSwatchV1(
                candidate.Red,
                candidate.Green,
                candidate.Blue,
                Math.Round(candidate.Weight / totalWeight, 6)))
            .ToArray();
    }

    private static byte[] SamplePixels(SKBitmap bitmap)
    {
        var totalPixels = (long)bitmap.Width * bitmap.Height;
        var stride = Math.Max(
            1,
            (int)Math.Ceiling(Math.Sqrt(totalPixels / (double)MaxSamples)));
        var columns = (bitmap.Width + stride - 1) / stride;
        var rows = (bitmap.Height + stride - 1) / stride;
        var samples = new byte[columns * rows * 4];
        var offset = 0;

        for (var y = 0; y < bitmap.Height; y += stride)
        {
            for (var x = 0; x < bitmap.Width; x += stride)
            {
                var color = bitmap.GetPixel(x, y);
                samples[offset++] = color.Red;
                samples[offset++] = color.Green;
                samples[offset++] = color.Blue;
                samples[offset++] = color.Alpha;
            }
        }

        return samples;
    }

    private void Remember(string key, ArtworkPaletteV1 palette)
    {
        lock (_cacheGate)
        {
            if (_cache.ContainsKey(key))
            {
                _cache[key] = palette;
                return;
            }

            while (_cache.Count >= MaxCacheEntries && _cacheOrder.Count > 0)
            {
                _cache.Remove(_cacheOrder.Dequeue());
            }

            _cache[key] = palette;
            _cacheOrder.Enqueue(key);
        }
    }

    private static double HueDegrees(byte red, byte green, byte blue)
    {
        var rn = red / 255d;
        var gn = green / 255d;
        var bn = blue / 255d;
        var max = Math.Max(rn, Math.Max(gn, bn));
        var min = Math.Min(rn, Math.Min(gn, bn));
        var delta = max - min;
        if (delta <= double.Epsilon)
        {
            return 0d;
        }

        double hue;
        if (Math.Abs(max - rn) <= double.Epsilon)
        {
            hue = ((gn - bn) / delta) % 6d;
        }
        else if (Math.Abs(max - gn) <= double.Epsilon)
        {
            hue = ((bn - rn) / delta) + 2d;
        }
        else
        {
            hue = ((rn - gn) / delta) + 4d;
        }

        return (hue * 60d + 360d) % 360d;
    }

    private static double HueDistance(double left, double right)
    {
        var distance = Math.Abs(left - right) % 360d;
        return distance > 180d ? 360d - distance : distance;
    }

    private sealed class PaletteBucket
    {
        public double Red { get; set; }

        public double Green { get; set; }

        public double Blue { get; set; }

        public double Weight { get; set; }
    }

    private sealed record PaletteCandidate(
        int Key,
        byte Red,
        byte Green,
        byte Blue,
        double Weight,
        double Hue);
}
