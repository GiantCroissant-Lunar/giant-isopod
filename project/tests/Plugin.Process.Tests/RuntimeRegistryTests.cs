using GiantIsopod.Contracts.Protocol.Runtime;

namespace GiantIsopod.Plugin.Process.Tests;

public class RuntimeRegistryTests
{
    private const string ValidRuntimesJson = """
    {
        "runtimes": [
            {
                "type": "cli",
                "id": "pi",
                "displayName": "Pi Agent",
                "executable": "pi",
                "args": ["--prompt", "{prompt}"]
            },
            {
                "type": "api",
                "id": "remote-api",
                "displayName": "Remote API",
                "baseUrl": "https://api.example.com"
            },
            {
                "type": "sdk",
                "id": "sdk-agent",
                "displayName": "SDK Agent",
                "sdkName": "my-sdk"
            }
        ]
    }
    """;

    private const string LegacyCliProvidersJson = """
    {
        "providers": [
            {
                "id": "legacy-cli",
                "displayName": "Legacy CLI",
                "executable": "legacy",
                "args": ["--run"],
                "env": { "API_KEY": "test" },
                "defaults": { "model": "gpt-4" }
            }
        ]
    }
    """;

    [Fact]
    public void LoadFromJson_DeserializesAllRuntimeTypes()
    {
        var registry = RuntimeRegistry.LoadFromJson(ValidRuntimesJson);

        Assert.Equal(3, registry.All.Count);
    }

    [Fact]
    public void LoadFromJson_CliRuntimeHasCorrectProperties()
    {
        var registry = RuntimeRegistry.LoadFromJson(ValidRuntimesJson);

        var cli = registry.Resolve("pi") as CliRuntimeConfig;
        Assert.NotNull(cli);
        Assert.Equal("pi", cli!.Executable);
        Assert.Equal("Pi Agent", cli.DisplayName);
        Assert.Contains("{prompt}", cli.Args);
    }

    [Fact]
    public void LoadFromJson_ApiRuntimeDeserializes()
    {
        var registry = RuntimeRegistry.LoadFromJson(ValidRuntimesJson);

        var api = registry.Resolve("remote-api") as ApiRuntimeConfig;
        Assert.NotNull(api);
        Assert.Equal("https://api.example.com", api!.BaseUrl);
    }

    [Fact]
    public void LoadFromJson_SdkRuntimeDeserializes()
    {
        var registry = RuntimeRegistry.LoadFromJson(ValidRuntimesJson);

        var sdk = registry.Resolve("sdk-agent") as SdkRuntimeConfig;
        Assert.NotNull(sdk);
        Assert.Equal("my-sdk", sdk!.SdkName);
    }

    [Fact]
    public void Resolve_CaseInsensitive()
    {
        var registry = RuntimeRegistry.LoadFromJson(ValidRuntimesJson);

        Assert.NotNull(registry.Resolve("PI"));
        Assert.NotNull(registry.Resolve("Pi"));
        Assert.NotNull(registry.Resolve("pi"));
    }

    [Fact]
    public void Resolve_UnknownId_ReturnsNull()
    {
        var registry = RuntimeRegistry.LoadFromJson(ValidRuntimesJson);

        Assert.Null(registry.Resolve("nonexistent"));
    }

    [Fact]
    public void ResolveOrDefault_WithId_ReturnsMatching()
    {
        var registry = RuntimeRegistry.LoadFromJson(ValidRuntimesJson);

        var result = registry.ResolveOrDefault("sdk-agent");
        Assert.Equal("sdk-agent", result.Id);
    }

    [Fact]
    public void ResolveOrDefault_NullId_ReturnsFirst()
    {
        var registry = RuntimeRegistry.LoadFromJson(ValidRuntimesJson);

        var result = registry.ResolveOrDefault(null);
        Assert.NotNull(result);
    }

    [Fact]
    public void ResolveOrDefault_EmptyRegistry_Throws()
    {
        var json = """{ "runtimes": [] }""";
        var registry = RuntimeRegistry.LoadFromJson(json);

        Assert.Throws<InvalidOperationException>(() => registry.ResolveOrDefault());
    }

    [Fact]
    public void LoadFromLegacyCliProviders_ConvertsToCliRuntimeConfig()
    {
        var registry = RuntimeRegistry.LoadFromLegacyCliProviders(LegacyCliProvidersJson);

        var cli = registry.Resolve("legacy-cli") as CliRuntimeConfig;
        Assert.NotNull(cli);
        Assert.Equal("legacy", cli!.Executable);
        Assert.Equal("Legacy CLI", cli.DisplayName);
        Assert.Equal("test", cli.Env["API_KEY"]);
        Assert.Equal("gpt-4", cli.Defaults["model"]);
    }
}
