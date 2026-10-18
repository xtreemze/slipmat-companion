namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Pure eligibility decision for an optional lossy remote derivative.
/// It never changes source files, playback ownership, or measured output claims.
/// </summary>
public static class OpusDeliveryPolicy
{
    public static OpusDeliveryDecision Select(
        OpusDeliveryRequest request)
    {
        if (!request.ClientCanDecodeOpus || !request.ServerCanEncodeOpus)
            return OpusDeliveryDecision.Original("codec-unavailable");

        if (!request.IsRemote || !request.BandwidthConstrained)
            return OpusDeliveryDecision.Original("source-preferred");

        if (request.RequiresLossless || request.IsDjPerformance ||
            request.IsMastering || request.RequiresBitPerfect)
            return OpusDeliveryDecision.Original("fidelity-required");

        if (!request.HasAuthorizedSource || !request.SourceIsAudio)
            return OpusDeliveryDecision.Original("source-unavailable");

        if (!request.OpusContainerSupported)
            return OpusDeliveryDecision.Original("container-unsupported");

        return new OpusDeliveryDecision(true, "opus-eligible");
    }
}

public sealed record OpusDeliveryRequest(
    bool ClientCanDecodeOpus,
    bool ServerCanEncodeOpus,
    bool OpusContainerSupported,
    bool IsRemote,
    bool BandwidthConstrained,
    bool RequiresLossless,
    bool IsDjPerformance,
    bool IsMastering,
    bool RequiresBitPerfect,
    bool HasAuthorizedSource,
    bool SourceIsAudio);

public sealed record OpusDeliveryDecision(bool UseOpusDerivative, string Reason)
{
    public static OpusDeliveryDecision Original(string reason) => new(false, reason);
}
