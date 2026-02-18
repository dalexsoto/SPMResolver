using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SPMResolver.Services;

public sealed class GitHubReleaseClient(HttpClient httpClient)
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<List<string>> DownloadReleaseAssetsAsync(string packageUrl, string? tag, string downloadDirectory, CancellationToken cancellationToken)
    {
        if (!TryParseGitHubUrl(packageUrl, out var owner, out var repo))
        {
            return [];
        }

        Console.WriteLine($"Checking GitHub Release assets for {owner}/{repo}...");
        Console.WriteLine(string.IsNullOrWhiteSpace(tag)
            ? "Release lookup mode: latest release."
            : $"Release lookup mode: tag '{tag}'.");

        GitHubRelease? release;
        try
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                release = await GetLatestReleaseAsync(owner, repo, cancellationToken);
            }
            else
            {
                release = await GetReleaseByTagAsync(owner, repo, tag, cancellationToken);
            }

            if (release is null)
            {
                Console.WriteLine("No release found.");
                return [];
            }

            if (!string.IsNullOrWhiteSpace(release.HtmlUrl))
            {
                Console.WriteLine($"Resolved GitHub release page: {release.HtmlUrl}");
            }

            var assets = FilterAssets(release.Assets);
            if (assets.Count == 0)
            {
                Console.WriteLine("No suitable binary assets found in release.");
                return [];
            }

            var downloadedFiles = new List<string>();
            var hadDownloadFailure = false;
            Directory.CreateDirectory(downloadDirectory);

            foreach (var asset in assets)
            {
                var filePath = Path.Combine(downloadDirectory, SanitizeFileName(asset.Name));
                try
                {
                    Console.WriteLine($"Found asset: {asset.Name}");
                    Console.WriteLine($"Downloading {asset.Name}...");

                    using var response = await _httpClient.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var fileStream = File.Create(filePath);
                    await stream.CopyToAsync(fileStream, cancellationToken);

                    downloadedFiles.Add(filePath);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    hadDownloadFailure = true;
                    Console.Error.WriteLine($"Warning: Failed to download release asset '{asset.Name}': {ex.Message}");
                    try
                    {
                        if (File.Exists(filePath))
                        {
                            File.Delete(filePath);
                        }
                    }
                    catch (Exception cleanupEx)
                    {
                        Console.Error.WriteLine($"Warning: Failed to clean up partial download '{filePath}': {cleanupEx.Message}");
                    }
                }
            }

            if (hadDownloadFailure)
            {
                Console.Error.WriteLine("Warning: One or more release assets failed to download. Falling back to source build.");
                return [];
            }

            return downloadedFiles;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Warning: GitHub API request failed: {ex.Message}");
            return [];
        }
    }

    private async Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        return await FetchReleaseAsync(url, cancellationToken);
    }

    private async Task<GitHubRelease?> GetReleaseByTagAsync(string owner, string repo, string tag, CancellationToken cancellationToken)
    {
        var escapedTag = Uri.EscapeDataString(tag);
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{escapedTag}";
        return await FetchReleaseAsync(url, cancellationToken);
    }

    private async Task<GitHubRelease?> FetchReleaseAsync(string url, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "SPMResolver");
        request.Headers.Add("Accept", "application/vnd.github.v3+json");

        if (Environment.GetEnvironmentVariable("GITHUB_TOKEN") is string token && !string.IsNullOrWhiteSpace(token))
        {
             request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: cancellationToken);
    }

    private static List<GitHubAsset> FilterAssets(List<GitHubAsset>? assets)
    {
        if (assets is null) return [];

        return assets.Where(a =>
            !string.IsNullOrWhiteSpace(a.Name) &&
            !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl) &&
            !IsSourceCodeAssetName(a.Name) &&
            IsSupportedArchiveAsset(a.Name)
        ).ToList();
    }

    private static bool IsSourceCodeAssetName(string name)
    {
        return string.Equals(name, "Source code (zip)", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Source code (tar.gz)", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedArchiveAsset(string assetName)
    {
        return assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
               assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
               assetName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ||
               assetName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseGitHubUrl(string url, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;

        const string sshPrefix = "git@github.com:";
        if (url.StartsWith(sshPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var sshPath = url.Substring(sshPrefix.Length);
            var sshSegments = sshPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (sshSegments.Length < 2)
            {
                return false;
            }

            owner = sshSegments[0];
            repo = TrimGitSuffix(sshSegments[1]);
            return !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Host, "www.github.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        owner = segments[0];
        repo = TrimGitSuffix(segments[1]);

        return !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo);
    }

    private static string TrimGitSuffix(string repo)
    {
        return repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? repo[..^4]
            : repo;
    }

    private static string SanitizeFileName(string fileName)
    {
        var sanitized = new string(fileName
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch)
            .ToArray())
            .Trim();

        return string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or ".."
            ? Guid.NewGuid().ToString("N")
            : sanitized;
    }

    private class GitHubRelease
    {
        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = "";

        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
    }
}
