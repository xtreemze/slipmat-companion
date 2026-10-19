using System;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.AudioGateway.Api;
using Jellyfin.Plugin.AudioGateway.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class RemoteSessionProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid OtherOwner = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    [Fact]
    public void Project_DeduplicatesSessionIdentityAndPrefersNewestObservation()
    {
        var result = RemoteSessionProjection.ProjectForUser(
        [
            new("one", "device-a", "Old", true, true, Now.AddSeconds(-10), Owner),
            new("one", "device-a", "New", true, true, Now.AddSeconds(-1), Owner),
            new("two", "device-b", "TV", true, true, Now, Owner)
        ], Owner, Now);
        Assert.False(result.Authoritative);
        Assert.False(result.Truncated);
        Assert.Equal(1, result.SchemaVersion);
        Assert.Equal(2, result.Sessions.Count);
        Assert.Equal("jellyfin-session:one", result.Sessions[0].Id);
        Assert.Equal("New", result.Sessions[0].Name);
        Assert.Equal("jellyfin-session:two", result.Sessions[1].Id);
    }

    [Fact]
    public void Project_FailsClosedForStaleFutureInactiveAndInvalidSessions()
    {
        var result = RemoteSessionProjection.ProjectForUser(
        [
            new("old", null, null, true, true, Now.AddSeconds(-46), Owner),
            new("future", null, null, true, true, Now.AddSeconds(1), Owner),
            new("inactive", null, null, false, true, Now, Owner),
            new("blocked", null, null, true, false, Now, Owner),
            new("../escape", null, null, true, true, Now, Owner),
            new("good", null, "TV\nName", true, true, Now, Owner)
        ], Owner, Now);
        Assert.Single(result.Sessions);
        Assert.Equal("TVName", result.Sessions.Single().Name);
    }

    [Fact]
    public void Project_DoesNotAdvertiseUndetectedDlnaOrContinuousStreaming()
    {
        var result = RemoteSessionProjection.ProjectForUser(
        [
            new("id", null, "DLNA renderer", true, true, Now, Owner)
        ], Owner, Now);
        var device = Assert.Single(result.Sessions);
        Assert.Equal("jellyfin-session", device.Kind);
        Assert.DoesNotContain("stream", device.Commands);
        Assert.DoesNotContain("crossfade", device.Commands);
    }

    [Fact]
    public void ProjectForUser_ExcludesSessionsOwnedByOtherUsersAndInvalidPrincipals()
    {
        var observations = new[]
        {
            new RemoteSessionProjection.Observation("owned", null, "My TV", true, true, Now, Owner),
            new RemoteSessionProjection.Observation("other", null, "Other TV", true, true, Now, OtherOwner)
        };

        var result = RemoteSessionProjection.ProjectForUser(observations, Owner, Now);
        Assert.Equal("jellyfin-session:owned", Assert.Single(result.Sessions).Id);
        Assert.Empty(RemoteSessionProjection.ProjectForUser(observations, Guid.Empty, Now).Sessions);
    }

    [Fact]
    public void Project_BoundsResultSize()
    {
        var observations = Enumerable.Range(0, 500)
            .Select(i => new RemoteSessionProjection.Observation(
                $"s{i}", null, "Device", true, true, Now, Owner));
        var result = RemoteSessionProjection.ProjectForUser(observations, Owner, Now);
        Assert.Equal(128, result.Sessions.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Project_SkipsStaleCandidatesBeforeFindingFreshSessionsWithinTheObservationBound()
    {
        var observations = Enumerable.Range(0, 150)
            .Select(i => new RemoteSessionProjection.Observation(
                $"stale-{i}", null, "Old device", true, true, Now.AddMinutes(-2), Owner))
            .Append(new RemoteSessionProjection.Observation(
                "fresh", null, "Fresh device", true, true, Now, Owner));

        var result = RemoteSessionProjection.ProjectForUser(observations, Owner, Now);

        Assert.False(result.Truncated);
        Assert.Equal("jellyfin-session:fresh", Assert.Single(result.Sessions).Id);
    }

    [Fact]
    public void Project_ReportsWhenTheObservationBoundStopsEnumeration()
    {
        var observations = Enumerable.Range(0, RemoteSessionProjection.MaximumObservations + 1)
            .Select(i => new RemoteSessionProjection.Observation(
                $"inactive-{i}", null, "Device", false, true, Now, Owner));

        var result = RemoteSessionProjection.ProjectForUser(observations, Owner, Now);

        Assert.Empty(result.Sessions);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void CurrentUserEndpoint_IsAuthenticatedAndDoesNotAcceptAnOwnerSelector()
    {
        var controller = typeof(MyRemoteSessionsController);
        var authorization = Assert.IsType<AuthorizeAttribute>(
            Attribute.GetCustomAttribute(controller, typeof(AuthorizeAttribute)));
        Assert.Null(authorization.Policy);
        Assert.Equal(
            "Plugins/AudioGateway/devices/sessions",
            Assert.IsType<RouteAttribute>(Attribute.GetCustomAttribute(controller, typeof(RouteAttribute))).Template);

        var action = controller.GetMethod(nameof(MyRemoteSessionsController.GetMine));
        Assert.NotNull(action);
        Assert.Empty(action!.GetParameters());
        Assert.Equal("mine", Assert.IsType<HttpGetAttribute>(
            Attribute.GetCustomAttribute(action, typeof(HttpGetAttribute))).Template);

        var fields = typeof(RemoteSessionProjection.Session).GetProperties().Select(property => property.Name);
        Assert.DoesNotContain(fields, name =>
            new[] { "user", "deviceid", "address", "url", "token", "secret" }
                .Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }
}
