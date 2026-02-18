using System.Text.Json.Serialization;

namespace SPMResolver.Tool.Models;

public sealed class SwiftDependencyNode
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("dependencies")]
    public List<SwiftDependencyNode> Dependencies { get; set; } = [];
}
