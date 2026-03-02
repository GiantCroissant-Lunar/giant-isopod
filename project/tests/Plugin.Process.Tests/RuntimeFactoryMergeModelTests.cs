using GiantIsopod.Contracts.Core;

namespace GiantIsopod.Plugin.Process.Tests;

public class RuntimeFactoryMergeModelTests
{
    [Fact]
    public void BothNull_ReturnsNull()
    {
        var result = RuntimeFactory.MergeModel(null, null);
        Assert.Null(result);
    }

    [Fact]
    public void ExplicitNull_ReturnsDefault()
    {
        var @default = new ModelSpec(Provider: "openai", ModelId: "gpt-4");

        var result = RuntimeFactory.MergeModel(null, @default);

        Assert.Same(@default, result);
    }

    [Fact]
    public void DefaultNull_ReturnsExplicit()
    {
        var @explicit = new ModelSpec(Provider: "anthropic", ModelId: "claude-4");

        var result = RuntimeFactory.MergeModel(@explicit, null);

        Assert.Same(@explicit, result);
    }

    [Fact]
    public void ExplicitFieldsOverrideDefault()
    {
        var @default = new ModelSpec(Provider: "openai", ModelId: "gpt-4");
        var @explicit = new ModelSpec(Provider: "anthropic", ModelId: null);

        var result = RuntimeFactory.MergeModel(@explicit, @default);

        Assert.NotNull(result);
        Assert.Equal("anthropic", result!.Provider);
        Assert.Equal("gpt-4", result.ModelId); // falls through to default
    }

    [Fact]
    public void ExplicitNullFields_FallThroughToDefault()
    {
        var @default = new ModelSpec(Provider: "openai", ModelId: "gpt-4");
        var @explicit = new ModelSpec(Provider: null, ModelId: null);

        var result = RuntimeFactory.MergeModel(@explicit, @default);

        Assert.NotNull(result);
        Assert.Equal("openai", result!.Provider);
        Assert.Equal("gpt-4", result.ModelId);
    }

    [Fact]
    public void ParametersMerged_ExplicitOverridesDefault()
    {
        var defaultParams = new Dictionary<string, string> { ["temperature"] = "0.7", ["max_tokens"] = "4096" };
        var explicitParams = new Dictionary<string, string> { ["temperature"] = "0.3" };
        var @default = new ModelSpec(Parameters: defaultParams);
        var @explicit = new ModelSpec(Parameters: explicitParams);

        var result = RuntimeFactory.MergeModel(@explicit, @default);

        Assert.NotNull(result?.Parameters);
        Assert.Equal("0.3", result!.Parameters!["temperature"]);
        Assert.Equal("4096", result.Parameters["max_tokens"]);
    }

    [Fact]
    public void ExplicitParametersNull_ReturnsDefaultParameters()
    {
        var defaultParams = new Dictionary<string, string> { ["temperature"] = "0.7" };
        var @default = new ModelSpec(Parameters: defaultParams);
        var @explicit = new ModelSpec(Parameters: null);

        var result = RuntimeFactory.MergeModel(@explicit, @default);

        Assert.NotNull(result?.Parameters);
        Assert.Equal("0.7", result!.Parameters!["temperature"]);
    }

    [Fact]
    public void DefaultParametersNull_ReturnsExplicitParameters()
    {
        var explicitParams = new Dictionary<string, string> { ["temperature"] = "0.3" };
        var @default = new ModelSpec(Parameters: null);
        var @explicit = new ModelSpec(Parameters: explicitParams);

        var result = RuntimeFactory.MergeModel(@explicit, @default);

        Assert.NotNull(result?.Parameters);
        Assert.Equal("0.3", result!.Parameters!["temperature"]);
    }
}
