using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>
/// Administrative browse/search surface for publishing selected iptv-org channels
/// into Jellyfin's native Live TV service. The catalog remains external evidence;
/// this controller never accepts arbitrary provider URLs or stream headers.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/AudioGateway/livetv/iptv-org")]
public sealed class IptvOrgLiveTvController : ControllerBase
{
    private const int MaxLimit = 100;
    private const int MaxOffset = 10000;
    private readonly IptvOrgCatalogService _catalog;
    private readonly IGuideManager _guideManager;

    public IptvOrgLiveTvController(
        IptvOrgCatalogService catalog,
        IGuideManager guideManager)
    {
        _catalog = catalog;
        _guideManager = guideManager;
    }

    /// <summary>Browse or search normalized iptv-org channels.</summary>
    [HttpGet("channels")]
    [ProducesResponseType(typeof(IptvOrgLiveTvBrowseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IptvOrgLiveTvBrowseResponse>> Browse(
        [FromQuery] string? q = null,
        [FromQuery] string? country = null,
        [FromQuery] string? category = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50)
    {
        var query = q?.Trim() ?? string.Empty;
        var countryCode = country?.Trim().ToUpperInvariant() ?? string.Empty;
        var categoryValue = category?.Trim() ?? string.Empty;
        if (query.Length > 160 ||
            countryCode.Length > 8 ||
            categoryValue.Length > 64 ||
            offset is < 0 or > MaxOffset ||
            limit is < 1 or > MaxLimit)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid iptv-org browse request");
        }

        var snapshot = await _catalog.GetAsync(HttpContext.RequestAborted)
            .ConfigureAwait(false);
        var published = (Plugin.Instance?.Configuration.IptvOrgLiveTvChannelIds ?? Array.Empty<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);

        IEnumerable<IptvOrgCatalogChannel> filtered = snapshot.Channels;
        if (countryCode.Length > 0)
        {
            filtered = filtered.Where(channel =>
                channel.CountryCode.Equals(countryCode, StringComparison.OrdinalIgnoreCase));
        }

        if (categoryValue.Length > 0)
        {
            filtered = filtered.Where(channel =>
                channel.Categories.Contains(categoryValue, StringComparer.OrdinalIgnoreCase));
        }

        if (query.Length > 0)
        {
            filtered = filtered.Where(channel => MatchesQuery(channel, query));
        }

        var materialized = filtered.ToArray();
        var page = materialized
            .Skip(offset)
            .Take(limit)
            .Select(channel => Project(channel, published.Contains(channel.Id)))
            .ToArray();

        return Ok(new IptvOrgLiveTvBrowseResponse(
            Version: IptvOrgCatalogService.ContractVersion,
            ObservedAtMs: snapshot.ObservedAt.ToUnixTimeMilliseconds(),
            Total: materialized.Length,
            Channels: page));
    }

    /// <summary>Force a provider refresh while retaining last-known-good evidence on failure.</summary>
    [HttpPost("catalog/refresh")]
    [ProducesResponseType(typeof(IptvOrgCatalogRefreshResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<IptvOrgCatalogRefreshResponse>> RefreshCatalog()
    {
        var result = await _catalog.RefreshAsync(HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(new IptvOrgCatalogRefreshResponse(
            ObservedAtMs: result.Snapshot.ObservedAt.ToUnixTimeMilliseconds(),
            ChannelCount: result.Snapshot.Channels.Count,
            UsedLastKnownGood: result.UsedLastKnownGood));
    }

    /// <summary>
    /// Refresh Jellyfin's native Live TV guide/channel projection after the admin
    /// saves a changed iptv-org publication selection.
    /// </summary>
    [HttpPost("refresh")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> Refresh()
    {
        await _guideManager.RefreshGuide(
                new Progress<double>(),
                HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return NoContent();
    }

    private static bool MatchesQuery(IptvOrgCatalogChannel channel, string query)
    {
        var comparison = StringComparison.OrdinalIgnoreCase;
        return channel.Name.Contains(query, comparison) ||
            channel.Id.Contains(query, comparison) ||
            (channel.Network?.Contains(query, comparison) ?? false) ||
            channel.AlternateNames.Any(value => value.Contains(query, comparison)) ||
            channel.Owners.Any(value => value.Contains(query, comparison)) ||
            channel.Categories.Any(value => value.Contains(query, comparison)) ||
            channel.Languages.Any(value => value.Contains(query, comparison)) ||
            channel.CountryCode.Contains(query, comparison);
    }

    private static IptvOrgLiveTvChannel Project(
        IptvOrgCatalogChannel channel,
        bool published)
        => new(
            Id: channel.Id,
            Name: channel.Name,
            CountryCode: channel.CountryCode,
            Network: channel.Network,
            Categories: channel.Categories,
            Languages: channel.Languages,
            LogoUrl: channel.LogoUrl,
            Logos: channel.Logos
                .Take(6)
                .Select(static logo => new IptvOrgLiveTvLogo(
                    Url: logo.Url,
                    Width: logo.Width,
                    Height: logo.Height,
                    Format: logo.Format,
                    Tags: logo.Tags,
                    InUse: logo.InUse))
                .ToArray(),
            StreamCount: channel.Streams.Count,
            HeaderDependentStreamCount: channel.Streams.Count(
                static stream => stream.Referrer is not null || stream.UserAgent is not null),
            HasGuide: channel.HasGuide,
            PublishedToJellyfin: published);
}
