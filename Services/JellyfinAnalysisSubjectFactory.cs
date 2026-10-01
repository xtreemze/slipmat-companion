using System;
using Jellyfin.Plugin.AudioGateway.Models;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Projects Jellyfin host/resource identity into the host-neutral analysis
/// subject contract. Jellyfin remains an adapter; the analyzer/store never
/// receives Jellyfin server objects or raw filesystem paths.
/// </summary>
public sealed class JellyfinAnalysisSubjectFactory
{
    private readonly IServerApplicationHost _applicationHost;

    public JellyfinAnalysisSubjectFactory(IServerApplicationHost applicationHost)
    {
        _applicationHost = applicationHost;
    }

    public AnalysisSubjectV1 ForItem(Guid itemId, string? representationId = null)
        => new(
            HostKind: "jellyfin",
            ProviderInstanceId: _applicationHost.SystemId.ToString(),
            ResourceId: itemId.ToString("N"),
            RepresentationId: representationId);

    public AnalysisSubjectV1 ForItem(string itemId, string? representationId = null)
    {
        if (!Guid.TryParse(itemId, out var parsed))
            throw new ArgumentException("Jellyfin artifact item ID must be a GUID.", nameof(itemId));

        return ForItem(parsed, representationId);
    }
}
