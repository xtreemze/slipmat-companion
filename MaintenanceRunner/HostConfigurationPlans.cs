using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;

namespace Slipmat.MaintenanceRunner;

/// <summary>Recomputes the only permitted promotion bytes from admitted pre-change XML.</summary>
public static class HostConfigurationPlans
{
    public static (CloudMaintenanceXmlPlan Profile, CloudMaintenanceXmlPlan Policy) Derive(
        HostSpecification spec, CloudMaintenanceAdmission admission, byte[] originalProfile, byte[] originalPolicy)
    {
        var binding = new CloudMaintenanceProfileBinding(admission.ProfileId, admission.RemoteName,
            admission.RemotePath, admission.LibraryName, admission.PreviousProjectionPath,
            admission.PreviousEnabled, admission.LocalPath);
        var profile = CloudMaintenanceProfileConfiguration.PrepareProfile(originalProfile, spec.Profile.BeforeHash, binding, promote: true);
        var policy = CloudMaintenanceProfileConfiguration.PreparePolicy(originalPolicy, spec.Policy.BeforeHash,
            admission.LocalPath, admission.PreviousLibraryScanPolicy, promote: true);
        if (!string.Equals(profile.AfterHash, spec.Profile.AfterHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(policy.AfterHash, spec.Policy.AfterHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("configuration-target-hash-mismatch");
        return (profile, policy);
    }
}
