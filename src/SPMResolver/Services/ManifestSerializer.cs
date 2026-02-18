using System.Text.Json;
using SPMResolver.Models;

namespace SPMResolver.Services;

public static class ManifestSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string Serialize(IEnumerable<ExportedDependency> dependencies)
    {
        var orderedDependencies = dependencies
            .OrderBy(dependency => dependency.Identity, StringComparer.Ordinal)
            .ToArray();

        var manifest = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            dependencies = orderedDependencies
        };

        return JsonSerializer.Serialize(manifest, SerializerOptions);
    }
}
