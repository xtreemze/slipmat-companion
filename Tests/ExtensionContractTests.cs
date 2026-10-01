using System;
using System.Linq;
using Jellyfin.Plugin.AudioGateway.Api;
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
            "Jellyfin.Plugin.AudioGateway.Api.PodcastSubscriptionsController",
            "Jellyfin.Plugin.AudioGateway.Api.PodcastResourceFetchController",
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
    public void PodcastResourceFetchController_RequiresAuthenticationAndExposesOnlyBoundedRequestContract()
    {
        var controller = typeof(PodcastResourceFetchController);
        Assert.NotNull(Attribute.GetCustomAttribute(controller, typeof(AuthorizeAttribute)));

        var fetch = controller.GetMethod(nameof(PodcastResourceFetchController.Fetch));
        Assert.NotNull(fetch);
        var parameters = fetch!.GetParameters();
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
