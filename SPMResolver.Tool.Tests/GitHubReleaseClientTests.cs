using System.Net;
using System.Text.Json;
using SPMResolver.Tool.Services;

namespace SPMResolver.Tool.Tests;

public class GitHubReleaseClientTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }

    [Fact]
    public async Task DownloadReleaseAssetsAsync_ShouldFindAndDownloadAssets()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var handler = new MockHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.Contains("/releases/latest"))
                {
                    var releaseJson = JsonSerializer.Serialize(new
                    {
                        tag_name = "v1.0.0",
                        assets = new[]
                        {
                            new { name = "lib.xcframework.zip", browser_download_url = "https://example.com/lib.xcframework.zip" },
                            new { name = "Source code (zip)", browser_download_url = "https://example.com/source.zip" }
                        }
                    });

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(releaseJson)
                    });
                }

                if (request.RequestUri!.AbsolutePath.EndsWith("lib.xcframework.zip"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });

            using var httpClient = new HttpClient(handler);
            var client = new GitHubReleaseClient(httpClient);

            var assets = await client.DownloadReleaseAssetsAsync("https://github.com/owner/repo", null, tempDir, CancellationToken.None);

            Assert.Single(assets);
            Assert.EndsWith("lib.xcframework.zip", assets[0]);
            Assert.True(File.Exists(assets[0]));
            Assert.Equal(4, new FileInfo(assets[0]).Length);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task DownloadReleaseAssetsAsync_ShouldIgnoreSourceCode()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        var sourceAssetRequestCount = 0;

        try
        {
            var handler = new MockHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.Contains("/releases/latest"))
                {
                    var releaseJson = JsonSerializer.Serialize(new
                    {
                        tag_name = "v1.0.0",
                        assets = new[]
                        {
                            new { name = "Source code (zip)", browser_download_url = "https://example.com/source.zip" },
                            new { name = "Source code (tar.gz)", browser_download_url = "https://example.com/source.tar.gz" }
                        }
                    });

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(releaseJson)
                    });
                }

                if (request.RequestUri!.AbsoluteUri.Contains("source", StringComparison.OrdinalIgnoreCase))
                {
                    sourceAssetRequestCount++;
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });

            using var httpClient = new HttpClient(handler);
            var client = new GitHubReleaseClient(httpClient);

            var assets = await client.DownloadReleaseAssetsAsync("https://github.com/owner/repo", null, tempDir, CancellationToken.None);

            Assert.Empty(assets);
            Assert.Equal(0, sourceAssetRequestCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task DownloadReleaseAssetsAsync_ShouldSupportSshGithubUrls()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        string? releaseEndpointPath = null;

        try
        {
            var handler = new MockHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.Contains("/releases/latest"))
                {
                    releaseEndpointPath = request.RequestUri.AbsolutePath;
                    var releaseJson = JsonSerializer.Serialize(new
                    {
                        tag_name = "v1.0.0",
                        assets = Array.Empty<object>()
                    });

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(releaseJson)
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });

            using var httpClient = new HttpClient(handler);
            var client = new GitHubReleaseClient(httpClient);

            var assets = await client.DownloadReleaseAssetsAsync("git@github.com:owner/repo.git", null, tempDir, CancellationToken.None);

            Assert.Empty(assets);
            Assert.Equal("/repos/owner/repo/releases/latest", releaseEndpointPath);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task DownloadReleaseAssetsAsync_ReturnsEmpty_WhenAnyAssetDownloadFails()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var handler = new MockHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.Contains("/releases/latest"))
                {
                    var releaseJson = JsonSerializer.Serialize(new
                    {
                        tag_name = "v1.0.0",
                        assets = new[]
                        {
                            new { name = "good.zip", browser_download_url = "https://example.com/good.zip" },
                            new { name = "bad.zip", browser_download_url = "https://example.com/bad.zip" }
                        }
                    });

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(releaseJson)
                    });
                }

                if (request.RequestUri!.AbsolutePath.EndsWith("/good.zip", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent([1, 2, 3])
                    });
                }

                if (request.RequestUri!.AbsolutePath.EndsWith("/bad.zip", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });

            using var httpClient = new HttpClient(handler);
            var client = new GitHubReleaseClient(httpClient);

            var assets = await client.DownloadReleaseAssetsAsync("https://www.github.com/owner/repo.git", null, tempDir, CancellationToken.None);

            Assert.Empty(assets);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
