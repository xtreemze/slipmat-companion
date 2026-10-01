using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

public record StreamripSearchRequest(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("limit")] int Limit = 25
);

public record StreamripSearchResult(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("desc")] string Description
);

public record StreamripSearchResponse(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("configuredSources")] IReadOnlyList<string> ConfiguredSources,
    [property: JsonPropertyName("results")] IReadOnlyList<StreamripSearchResult> Results,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("notes")] IReadOnlyList<string> Notes
);

public record StreamripServiceStatus(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("command")] string? Command,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("configPath")] string? ConfigPath,
    [property: JsonPropertyName("configuredSources")] IReadOnlyList<string> ConfiguredSources,
    [property: JsonPropertyName("notes")] IReadOnlyList<string> Notes
);
