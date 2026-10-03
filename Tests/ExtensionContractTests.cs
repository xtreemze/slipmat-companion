using System;
using System.Linq;
using Jellyfin.Plugin.AudioGateway.Api;
using Jellyfin.Plugin.AudioGateway.Configuration;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class ExtensionContractTests
{
    [Fact]
    public void ShippingPluginAssembly_ExcludesAuthorityBearingAtlasAndProviderTypes()
    {
        var shippingAssembly = typeof(Plugin).Assembly;
        var excludedTypeNames = new[]
        {
            "Jellyfin.Plugin.AudioGateway.Api.AtlasController",
            "Jellyfin.Plugin.AudioGateway.Api.AtlasRequestsController",
            "Jellyfin.Plugin.AudioGateway.Api.StreamripController",
            "Jellyfin.Plugin.AudioGateway.Services.AtlasCatalogStoreService",
            "Jellyfin.Plugin.AudioGateway.Services.AtlasConceptPayloadFactory",
            "Jellyfin.Plugin.AudioGateway.Services.AtlasRequestMaterializationService",
            "Jellyfin.Plugin.AudioGateway.Services.AtlasRequestStoreService",
            "Jellyfin.Plugin.AudioGateway.Services.StreamripCliService",
        };

        Assert.All(
            excludedTypeNames,
            typeName => Assert.Null(shippingAssembly.GetType(typeName, throwOnError: false)));
    }

    [Fact]
    public void ExperimentalAuthorityBearingControllers_RemainNonRoutableInTests()
    {
        var quarantinedControllers = new[]
        {
            typeof(AtlasController),
            typeof(AtlasRequestsController),
            typeof(StreamripController),
        };

        Assert.All(
            quarantinedControllers,
            controller => Assert.NotNull(
                Attribute.GetCustomAttribute(controller, typeof(NonControllerAttribute))));
    }

    [Fact]
    public void OptionalFactAndArtifactControllers_RemainInShippingAssemblyAndRoutable()
    {
        var shippingAssembly = typeof(Plugin).Assembly;
        var activeTypeNames = new[]
        {
            "Jellyfin.Plugin.AudioGateway.Api.CapabilitiesController",
            "Jellyfin.Plugin.AudioGateway.Api.ArtifactsController",
            "Jellyfin.Plugin.AudioGateway.Api.EventsController",
            "Jellyfin.Plugin.AudioGateway.Api.ArtworkPaletteController",
            "Jellyfin.Plugin.AudioGateway.Api.PodcastSubscriptionsController",
            "Jellyfin.Plugin.AudioGateway.Api.PodcastDirectoryController",
            "Jellyfin.Plugin.AudioGateway.Api.PodcastResourceFetchController",
            "Jellyfin.Plugin.AudioGateway.Api.DirectoryResourceFetchController",
            "Jellyfin.Plugin.AudioGateway.Api.RemoteMediaRelayController",
            "Jellyfin.Plugin.AudioGateway.Api.IptvOrgLiveTvController",
            "Jellyfin.Plugin.AudioGateway.Api.IptvOrgGuideResourceFetchController",
            "Jellyfin.Plugin.AudioGateway.Api.CloudProjectionController",
            "Jellyfin.Plugin.AudioGateway.Api.CloudMigrationController",
        };

        Assert.All(
            activeTypeNames,
            typeName =>
            {
                var controller = shippingAssembly.GetType(typeName, throwOnError: false);
                Assert.NotNull(controller);
                Assert.Null(Attribute.GetCustomAttribute(controller!, typeof(NonControllerAttribute)));
            });
    }

    [Fact]
    public void PodcastSubscriptionController_RequiresAuthenticationAndAcceptsNoUserSelector()
    {
        var controller = typeof(PodcastSubscriptionsController);
        Assert.NotNull(Attribute.GetCustomAttribute(controller, typeof(AuthorizeAttribute)));

        var publicMethods = controller
            .GetMethods()
            .Where(method => method.DeclaringType == controller)
            .Where(method => method.Name is nameof(PodcastSubscriptionsController.GetSubscriptions) or nameof(PodcastSubscriptionsController.Sync));

        Assert.All(
            publicMethods,
            method => Assert.DoesNotContain(
                method.GetParameters(),
                parameter => parameter.Name?.Contains("user", StringComparison.OrdinalIgnoreCase) == true));
    }

    [Fact]
    public void PodcastDirectoryController_RequiresAuthenticationAndExposesOnlyBoundedSearchContract()
    {
        AssertSingleAuthenticatedRequest(typeof(PodcastDirectoryController), nameof(PodcastDirectoryController.Search));
    }

    [Fact]
    public void PodcastResourceFetchController_RequiresAuthenticationAndExposesOnlyBoundedRequestContract()
    {
        AssertSingleAuthenticatedRequest(typeof(PodcastResourceFetchController), nameof(PodcastResourceFetchController.Fetch));
    }

    [Fact]
    public void DirectoryResourceFetchController_RequiresAuthenticationAndExposesOnlyBoundedRequestContract()
    {
        AssertSingleAuthenticatedRequest(typeof(DirectoryResourceFetchController), nameof(DirectoryResourceFetchController.Fetch));
    }

    [Fact]
    public void IptvOrgGuideResourceFetchController_RequiresLiveTvAccessAndExposesOnlyBoundedRequestContract()
    {
        var controller = typeof(IptvOrgGuideResourceFetchController);
        var authorize = Assert.IsType<AuthorizeAttribute>(
            Attribute.GetCustomAttribute(controller, typeof(AuthorizeAttribute)));
        Assert.Equal(Policies.LiveTvAccess, authorize.Policy);

        var fetch = controller.GetMethod(nameof(IptvOrgGuideResourceFetchController.Fetch));
        Assert.NotNull(fetch);
        Assert.Single(fetch!.GetParameters());
        Assert.Equal("request", fetch.GetParameters()[0].Name);

        var requestProperties = typeof(Jellyfin.Plugin.AudioGateway.Models.IptvOrgGuideResourceFetchRequest)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "Url", "Version" }, requestProperties);
    }

    [Fact]
    public void IptvOrgLiveTvController_RequiresElevationAndAcceptsOnlyBoundedBrowseInputs()
    {
        var controller = typeof(IptvOrgLiveTvController);
        var authorize = Assert.IsType<AuthorizeAttribute>(
            Attribute.GetCustomAttribute(controller, typeof(AuthorizeAttribute)));
        Assert.Equal(Policies.RequiresElevation, authorize.Policy);

        var browse = controller.GetMethod(nameof(IptvOrgLiveTvController.Browse));
        Assert.NotNull(browse);
        Assert.All(
            browse!.GetParameters(),
            parameter => Assert.DoesNotContain(
                new[] { "url", "header", "cookie", "secret", "useragent", "referrer" },
                fragment => parameter.Name?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true));

        Assert.Empty(controller.GetMethod(nameof(IptvOrgLiveTvController.Refresh))!.GetParameters());
        Assert.Empty(controller.GetMethod(nameof(IptvOrgLiveTvController.RefreshCatalog))!.GetParameters());
    }

    [Fact]
    public void CloudProjectionController_RequiresElevationAndAcceptsNoCredentialInputs()
    {
        var controller = typeof(CloudProjectionController);
        var authorize = Assert.IsType<AuthorizeAttribute>(
            Attribute.GetCustomAttribute(controller, typeof(AuthorizeAttribute)));
        Assert.Equal(Policies.RequiresElevation, authorize.Policy);

        var methods = controller
            .GetMethods()
            .Where(method => method.DeclaringType == controller);

        Assert.All(
            methods
                .SelectMany(method => method.GetParameters())
                .Where(parameter => parameter.ParameterType != typeof(System.Threading.CancellationToken)),
            parameter => Assert.DoesNotContain(
                new[] { "token", "password", "credential", "cookie", "authorization", "secret" },
                fragment => parameter.Name?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true));
    }

    [Fact]
    public void CloudMigrationController_RequiresElevation()
    {
        var controller = typeof(CloudMigrationController);
        var authorize = Assert.IsType<AuthorizeAttribute>(
            Attribute.GetCustomAttribute(controller, typeof(AuthorizeAttribute)));
        Assert.Equal(Policies.RequiresElevation, authorize.Policy);
    }

    [Fact]
    public void CloudConfiguration_ContainsNoCredentialsOrExecutablePath()
    {
        var properties = typeof(PluginConfiguration)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(
            properties,
            name => name.Contains("RcloneConfig", StringComparison.OrdinalIgnoreCase)
                || name.Contains("RcloneExecutable", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            properties,
            name => new[] { "token", "password", "credential", "cookie", "secret" }
                .Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void RemoteMediaRelayController_RequiresAuthenticationAndDoesNotExposeGenericProxyInputs()
    {
        var controller = typeof(RemoteMediaRelayController);
        Assert.NotNull(Attribute.GetCustomAttribute(controller, typeof(AuthorizeAttribute)));

        var prepare = controller.GetMethod(nameof(RemoteMediaRelayController.Prepare));
        Assert.NotNull(prepare);
        Assert.Single(prepare!.GetParameters());
        Assert.Equal("request", prepare.GetParameters()[0].Name);

        var relay = controller.GetMethod(nameof(RemoteMediaRelayController.Relay));
        Assert.NotNull(relay);
        var relayParameters = relay!.GetParameters();
        Assert.Single(relayParameters);
        Assert.Equal("relayId", relayParameters[0].Name);

        Assert.All(
            prepare.GetParameters().Concat(relayParameters),
            parameter => Assert.DoesNotContain(
                new[] { "header", "cookie", "method", "user", "target", "url" },
                fragment => parameter.Name?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true));
    }

    private static void AssertSingleAuthenticatedRequest(Type controller, string methodName)
    {
        Assert.NotNull(Attribute.GetCustomAttribute(controller, typeof(AuthorizeAttribute)));

        var method = controller.GetMethod(methodName);
        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Single(parameters);
        Assert.Equal("request", parameters[0].Name);
        Assert.DoesNotContain(
            parameters,
            parameter => parameter.Name?.Contains("header", StringComparison.OrdinalIgnoreCase) == true ||
                         parameter.Name?.Contains("cookie", StringComparison.OrdinalIgnoreCase) == true ||
                         parameter.Name?.Contains("method", StringComparison.OrdinalIgnoreCase) == true ||
                         parameter.Name?.Contains("user", StringComparison.OrdinalIgnoreCase) == true);
    }
}
