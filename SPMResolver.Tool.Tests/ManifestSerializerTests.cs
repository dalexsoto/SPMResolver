using System.Text.Json;
using SPMResolver.Tool.Models;
using SPMResolver.Tool.Services;

namespace SPMResolver.Tool.Tests;

public class ManifestSerializerTests
{
    [Fact]
    public void Serialize_OrdersDependenciesByIdentity()
    {
        var dependencies = new[]
        {
            new ExportedDependency("b", "zeta", "https://example.com/zeta.git", "/tmp/zeta", "/out/zeta", "1.0.0", "rev-z", null),
            new ExportedDependency("a", "alpha", "https://example.com/alpha.git", "/tmp/alpha", "/out/alpha", "1.0.0", "rev-a", null)
        };

        var json = ManifestSerializer.Serialize(dependencies);
        using var document = JsonDocument.Parse(json);
        var serializedDependencies = document.RootElement.GetProperty("dependencies").EnumerateArray().ToArray();

        Assert.Equal("alpha", serializedDependencies[0].GetProperty("identity").GetString());
        Assert.Equal("zeta", serializedDependencies[1].GetProperty("identity").GetString());
    }
}
