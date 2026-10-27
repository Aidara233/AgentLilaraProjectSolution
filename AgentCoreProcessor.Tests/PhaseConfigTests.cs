using AgentCoreProcessor.Engine.Vision;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class PhaseConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "phase-config-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_root, "nested", "phase.json");

    [Fact]
    public void MissingFileWritesAndReturnsProvidedDefaults()
    {
        var defaults = PhaseConfig.Phase1Default();
        Assert.Same(defaults, PhaseConfig.Load(ConfigPath, defaults));
        Assert.True(File.Exists(ConfigPath));
        var loaded = PhaseConfig.Load(ConfigPath, new());
        Assert.Equal(defaults.Model, loaded.Model);
        Assert.Equal(defaults.Endpoint, loaded.Endpoint);
        Assert.Equal(defaults.Temperature, loaded.Temperature);
        Assert.Equal(defaults.MaxTokens, loaded.MaxTokens);
        Assert.Equal(defaults.PromptTemplate, loaded.PromptTemplate);
    }

    [Fact]
    public void ValidFileOverridesDefaultsAndRoundTripsUnicodePrompt()
    {
        var config = new PhaseConfig { Model = "test-model", Endpoint = "https://example.invalid", Temperature = 0.7,
            MaxTokens = 321, PromptTemplate = "第一行\n{ContextText} 第二行" };
        config.Save(ConfigPath);
        var loaded = PhaseConfig.Load(ConfigPath, PhaseConfig.Phase2Default());
        Assert.Equal(config.Model, loaded.Model);
        Assert.Equal(config.Endpoint, loaded.Endpoint);
        Assert.Equal(config.Temperature, loaded.Temperature);
        Assert.Equal(config.MaxTokens, loaded.MaxTokens);
        Assert.Equal(config.PromptTemplate, loaded.PromptTemplate);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{invalid")]
    public void InvalidOrNullFileReturnsDefaultsWithoutOverwritingSource(string source)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, source);
        var defaults = PhaseConfig.Phase3Default();
        Assert.Same(defaults, PhaseConfig.Load(ConfigPath, defaults));
        Assert.Equal(source, File.ReadAllText(ConfigPath));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
