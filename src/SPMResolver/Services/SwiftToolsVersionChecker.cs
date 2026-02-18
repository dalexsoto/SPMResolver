using System.Text.RegularExpressions;

namespace SPMResolver.Services;

public static partial class SwiftToolsVersionChecker
{
    public static string? GetVersionWarning(string packageRootPath, string swiftVersionOutput)
    {
        var packageManifestPath = Path.Combine(packageRootPath, "Package.swift");
        if (!File.Exists(packageManifestPath))
        {
            return null;
        }

        var toolsVersion = ParseToolsVersion(File.ReadAllText(packageManifestPath));
        var swiftMajorVersion = ParseSwiftMajorVersion(swiftVersionOutput);

        if (toolsVersion is null || swiftMajorVersion is null)
        {
            return null;
        }

        if (swiftMajorVersion.Value < toolsVersion.Value)
        {
            return $"Package.swift declares swift-tools-version {toolsVersion}, but installed Swift major version is {swiftMajorVersion}.";
        }

        return null;
    }

    public static int? ParseToolsVersion(string packageManifest)
    {
        var match = ToolsVersionRegex().Match(packageManifest);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups[1].Value, out var majorVersion) ? majorVersion : null;
    }

    public static int? ParseSwiftMajorVersion(string swiftVersionOutput)
    {
        var match = SwiftVersionRegex().Match(swiftVersionOutput);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups[1].Value, out var majorVersion) ? majorVersion : null;
    }

    [GeneratedRegex(@"swift-tools-version:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ToolsVersionRegex();

    [GeneratedRegex(@"Swift version\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SwiftVersionRegex();
}
