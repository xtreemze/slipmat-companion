using System;

namespace Jellyfin.Plugin.AudioGateway.Services.Packaging;

/// <summary>
/// Operator-managed source keys for packaging captures. These are read from
/// the server environment, never stored in plugin configuration: the plugin's
/// configuration carries no credentials by policy.
/// </summary>
public sealed record PackagingSourceKeys(string AudioDbApiKey, string? DiscogsToken)
{
    /// <summary>A TheAudioDB key; unset uses the public, rate-limited test key.</summary>
    public const string AudioDbApiKeyEnvironmentVariable = "SLIPMAT_AUDIODB_API_KEY";

    /// <summary>A Discogs personal access token; unset leaves Discogs out.</summary>
    public const string DiscogsTokenEnvironmentVariable = "SLIPMAT_DISCOGS_TOKEN";

    /// <summary>TheAudioDB's public free key (the old "2" no longer answers).</summary>
    public const string PublicAudioDbKey = "123";

    public static PackagingSourceKeys FromEnvironment()
        => From(
            Environment.GetEnvironmentVariable(AudioDbApiKeyEnvironmentVariable),
            Environment.GetEnvironmentVariable(DiscogsTokenEnvironmentVariable));

    public static PackagingSourceKeys From(string? audioDbApiKey, string? discogsToken)
        => new(
            string.IsNullOrWhiteSpace(audioDbApiKey) ? PublicAudioDbKey : audioDbApiKey.Trim(),
            string.IsNullOrWhiteSpace(discogsToken) ? null : discogsToken.Trim());
}
