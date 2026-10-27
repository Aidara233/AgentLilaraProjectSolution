using System.Reflection;
using AgentCoreProcessor.Adapter;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class AdapterFactoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "factory-" + Guid.NewGuid().ToString("N"));
    private sealed class Media : IAdapterMediaStore
    {
        public Task<(string LocalPath, string Hash)> DownloadAndSaveAsync(string url, HttpClient client) => throw new NotSupportedException();
        public Task SetCategoryAsync(string hash, string category) => Task.CompletedTask;
    }
    private AdapterFactory Factory => new(_root, new Media(), path => Task.FromResult((path, "hash")), time => time);
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;

    [Fact]
    public void DefaultFileDirectoryUsesInjectedStorageRoot()
    {
        var adapter = Assert.IsType<FileAdapter>(Factory.Create(new() { Id = "file", Type = "FILE" }));
        Assert.Equal(Path.Combine(_root, "FileAdapter", "input"), Field<string>(adapter, "inputDir"));
        Assert.Equal(Path.Combine(_root, "FileAdapter", "output"), Field<string>(adapter, "outputDir"));
        Assert.Equal(2000, Field<int>(adapter, "pollIntervalMs"));
    }

    [Fact]
    public void FileAliasesAndCanonicalPrecedenceArePreserved()
    {
        var settings = JObject.Parse("""{"input_dir":"alias-input","output_dir":"alias-output","poll_ms":23}""");
        var alias = Factory.Create(new() { Id = "file", Type = "file", Settings = settings });
        Assert.Equal(Path.Combine("alias-input", "input"), Field<string>(alias, "inputDir"));
        Assert.Equal("alias-output", Field<string>(alias, "outputDir"));
        Assert.Equal(23, Field<int>(alias, "pollIntervalMs"));
        settings["baseDir"] = "canonical-input";
        settings["outputDir"] = "canonical-output";
        settings["pollIntervalMs"] = 41;
        var canonical = Factory.Create(new() { Id = "file", Type = "file", Settings = settings });
        Assert.Equal(Path.Combine("canonical-input", "input"), Field<string>(canonical, "inputDir"));
        Assert.Equal("canonical-output", Field<string>(canonical, "outputDir"));
        Assert.Equal(41, Field<int>(canonical, "pollIntervalMs"));
    }

    [Fact]
    public async Task HttpReloadUsesInjectedStorageConfigPath()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Adapter"));
        File.WriteAllText(Path.Combine(_root, "Adapter", "bridge.json"), """{"settings":{"defaultChannel":"reloaded"}}""");
        var adapter = Assert.IsType<HttpBridgeAdapter>(Factory.Create(new() { Id = "bridge", Type = "http" }));
        Assert.True(await adapter.ReloadConfigAsync());
        Assert.Equal("reloaded", Field<HttpBridgeConfig>(adapter, "config").DefaultChannel);
    }

    [Fact]
    public async Task PlatformSelectionPassesMediaPortWithoutConnecting()
    {
        var onebot = Assert.IsType<OneBotAdapter>(Factory.Create(new() { Id = "one", Type = "onebot" }));
        var icalingua = Assert.IsType<IcalinguaAdapter>(Factory.Create(new() { Id = "ica", Type = "icalingua" }));
        try
        {
            Assert.IsType<Media>(onebot.MediaStore);
            Assert.IsType<Media>(icalingua.MediaStore);
            Assert.Throws<ArgumentException>(() => Factory.Create(new() { Type = "missing" }));
        }
        finally { onebot.HttpClient.Dispose(); await icalingua.StopAsync(); icalingua.HttpClient.Dispose(); }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
