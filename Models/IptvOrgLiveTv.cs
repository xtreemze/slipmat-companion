using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>One retained iptv-org logo variant with its original geometry.</summary>
public sealed record IptvOrgLiveTvLogo(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("format")] string? Format,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("inUse")] bool InUse);

/// <summary>
/// One normalized iptv-org discovery result presented by the companion dashboard.
/// The provider-local channel ID is evidence; it is not a Slipmat canonical media ID.
/// </summary>
public sealed record IptvOrgLiveTvChannel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("countryCode")] string CountryCode,
    [property: JsonPropertyName("network")] string? Network,
    [property: JsonPropertyName("categories")] IReadOnlyList<string> Categories,
    [property: JsonPropertyName("languages")] IReadOnlyList<string> Languages,
    [property: JsonPropertyName("logoUrl")] string? LogoUrl,
    [property: JsonPropertyName("logos")] IReadOnlyList<IptvOrgLiveTvLogo> Logos,
    [property: JsonPropertyName("streamCount")] int StreamCount,
    [property: JsonPropertyName("headerDependentStreamCount")] int HeaderDependentStreamCount,
    [property: JsonPropertyName("hasGuide")] bool HasGuide,
    [property: JsonPropertyName("publishedToJellyfin")] bool PublishedToJellyfin);

/// <summary>Bounded dashboard browse/search result for iptv-org.</summary>
public sealed record IptvOrgLiveTvBrowseResponse(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("observedAtMs")] long ObservedAtMs,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("channels")] IReadOnlyList<IptvOrgLiveTvChannel> Channels);

/// <summary>Result of an explicit catalog refresh requested by a Jellyfin administrator.</summary>
public sealed record IptvOrgCatalogRefreshResponse(
    [property: JsonPropertyName("observedAtMs")] long ObservedAtMs,
    [property: JsonPropertyName("channelCount")] int ChannelCount,
    [property: JsonPropertyName("usedLastKnownGood")] bool UsedLastKnownGood);

/// <summary>
/// Narrow request for one iptv-org-published XMLTV source. The URL is accepted
/// only when it is present in the current normalized iptv-org guide catalog.
/// </summary>
public sealed record IptvOrgGuideResourceFetchRequest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("url")] string Url);

/// <summary>Bounded raw XMLTV bytes returned to the client-owned guide parser.</summary>
public sealed record IptvOrgGuideResourceFetchResponse(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("finalUrl")] string FinalUrl,
    [property: JsonPropertyName("contentType")] string? ContentType,
    [property: JsonPropertyName("bodyBase64")] string BodyBase64,
    [property: JsonPropertyName("lengthBytes")] long LengthBytes);

/// <summary>
/// Server-wide iptv-org channel publication selection. This configures the
/// optional Jellyfin Live TV projection; it is not a replacement for Slipmat's
/// client-owned per-user subscription intent.
/// </summary>
public sealed record IptvOrgLiveTvSelectionRequest(
    [property: JsonPropertyName("channelIds")] IReadOnlyList<string> ChannelIds);

/// <summary>Current server-wide iptv-org Live TV publication state.</summary>
public sealed record IptvOrgLiveTvSelectionResponse(
    [property: JsonPropertyName("channelIds")] IReadOnlyList<string> ChannelIds);
