using System;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Api;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class RemoteSessionsContractTests
{
    [Fact]
    public void Inventory_RequiresElevationAndIsReadOnly()
    {
        var controller = typeof(RemoteSessionsController);
        var authorization = Assert.IsType<AuthorizeAttribute>(
            Attribute.GetCustomAttribute(controller, typeof(AuthorizeAttribute)));
        Assert.Equal(Policies.RequiresElevation, authorization.Policy);

        var method = controller.GetMethod(nameof(RemoteSessionsController.GetSessions));
        Assert.NotNull(method);
        Assert.Empty(method!.GetParameters());
        var get = Assert.IsType<HttpGetAttribute>(
            Attribute.GetCustomAttribute(method, typeof(HttpGetAttribute)));
        Assert.Equal("sessions", get.Template);
        Assert.DoesNotContain(
            controller.GetMethods().Where(m => m.DeclaringType == controller),
            m => m.Name is "Post" or "Put" or "Delete");
    }

    [Fact]
    public void Snapshot_UsesNamespacedIdentityAndNoNetworkOrCredentialFields()
    {
        var snapshot = new RemoteSessionInventory(
            Version: 1,
            Authoritative: false,
            Truncated: false,
            Sessions: new[]
            {
                new RemoteSessionEntry(
                    Id: "jellyfin-session:session-1",
                    DisplayName: "Living room",
                    Source: "jellyfin-session",
                    SupportsRemoteControl: true),
            });

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.False(root.GetProperty("authoritative").GetBoolean());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        var entry = Assert.Single(root.GetProperty("sessions").EnumerateArray().ToArray());
        Assert.Equal("jellyfin-session:session-1", entry.GetProperty("id").GetString());
        Assert.Equal("jellyfin-session", entry.GetProperty("source").GetString());

        var properties = typeof(RemoteSessionEntry).GetProperties().Select(x => x.Name).ToArray();
        Assert.DoesNotContain(properties, name =>
            new[] { "token", "url", "address", "ip", "password", "secret", "deviceid", "userid" }
                .Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }
}
