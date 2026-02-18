using System.Text.Json;
using SPMResolver.Tool.Models;

namespace SPMResolver.Tool.Services;

public static class PackageResolvedReader
{
    public static IReadOnlyDictionary<string, ResolvedPin> ReadPins(string packageRootPath)
    {
        var packageResolvedPath = FindPackageResolvedPath(packageRootPath);
        if (packageResolvedPath is null)
        {
            return new Dictionary<string, ResolvedPin>(StringComparer.Ordinal);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(packageResolvedPath));
        var pinsElement = GetPinsElement(document.RootElement);
        if (pinsElement is null)
        {
            return new Dictionary<string, ResolvedPin>(StringComparer.Ordinal);
        }

        var pinsByLocation = new Dictionary<string, ResolvedPin>(StringComparer.Ordinal);
        foreach (var pinElement in pinsElement.Value.EnumerateArray())
        {
            if (!TryReadPin(pinElement, out var pin))
            {
                continue;
            }

            pinsByLocation[NormalizeLocation(pin.Location)] = pin;
        }

        return pinsByLocation;
    }

    public static string NormalizeLocation(string location)
    {
        var normalized = location.Trim();
        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return normalized.TrimEnd('/').ToLowerInvariant();
    }

    private static string? FindPackageResolvedPath(string packageRootPath)
    {
        var directPath = Path.Combine(packageRootPath, "Package.resolved");
        if (File.Exists(directPath))
        {
            return directPath;
        }

        var swiftPmPath = Path.Combine(packageRootPath, ".swiftpm", "Package.resolved");
        return File.Exists(swiftPmPath) ? swiftPmPath : null;
    }

    private static JsonElement? GetPinsElement(JsonElement root)
    {
        if (root.TryGetProperty("pins", out var directPins))
        {
            return directPins;
        }

        if (root.TryGetProperty("object", out var objectNode) && objectNode.TryGetProperty("pins", out var nestedPins))
        {
            return nestedPins;
        }

        return null;
    }

    private static bool TryReadPin(JsonElement pinElement, out ResolvedPin pin)
    {
        pin = default!;

        var identity = TryGetString(pinElement, "identity");
        var location = TryGetString(pinElement, "location") ?? TryGetString(pinElement, "repositoryURL");

        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        identity ??= GetFallbackIdentity(location);

        string? version = null;
        string? revision = null;
        string? branch = null;
        if (pinElement.TryGetProperty("state", out var state))
        {
            version = TryGetString(state, "version");
            revision = TryGetString(state, "revision");
            branch = TryGetString(state, "branch");
        }

        pin = new ResolvedPin(identity, location, version, revision, branch);
        return true;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static string GetFallbackIdentity(string location)
    {
        var trimmed = location.TrimEnd('/');
        var lastSegment = trimmed.Split('/').Last();
        return lastSegment.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? lastSegment[..^4]
            : lastSegment;
    }
}
