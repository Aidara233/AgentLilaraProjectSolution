using AgentCoreProcessor.Engine.Vision;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class VisionConfigStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vision-store-" + Guid.NewGuid().ToString("N"));
    private string PathFor(string name) => Path.Combine(_root, name, "VisionEngineConfig.json");

    [Fact]
    public void MissingFileWritesAllOriginalDefaultFields()
    {
        var path = PathFor("default");
        var config = new VisionConfigStore<VisionEngineConfigData>(path).Load();
        var expected = JObject.Parse("""
            {"visionConcurrency":3,"ocrConcurrency":4,"visionRetryCount":2,"visionRetryDelayMs":3000,
             "batchSize":10,"ocrEnabled":true,"visionEnabled":true,"ocrRichTextThreshold":80,
             "refineTriggerCount":3,"phase1Concurrency":5,"phase2Concurrency":2}
            """);
        Assert.True(JToken.DeepEquals(expected, JObject.Parse(File.ReadAllText(path))));
        Assert.True(JToken.DeepEquals(expected, JObject.FromObject(config)));
    }

    [Fact]
    public void AllFieldsRoundTripAndHostSubtypeUsesSameJsonShape()
    {
        var config = new VisionEngineConfig
        {
            VisionConcurrency = 7, OcrConcurrency = 8, VisionRetryCount = 9, VisionRetryDelayMs = 11,
            BatchSize = 12, OcrEnabled = false, VisionEnabled = false, OcrRichTextThreshold = 13,
            RefineTriggerCount = 14, Phase1Concurrency = 15, Phase2Concurrency = 16
        };
        var store = new VisionConfigStore<VisionEngineConfig>(PathFor("roundtrip"));
        store.Save(config);
        Assert.True(JToken.DeepEquals(JObject.FromObject(config), JObject.FromObject(store.Load())));
        Assert.Equal(11, JObject.FromObject(config).Count);
        Assert.Equal(JsonConvert.SerializeObject(config, Formatting.Indented), File.ReadAllText(PathFor("roundtrip")));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{broken")]
    public void InvalidFileReturnsDefaultsAndPreservesOriginal(string source)
    {
        var path = PathFor("invalid");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source);
        var config = new VisionConfigStore<VisionEngineConfigData>(path).Load();
        Assert.True(JToken.DeepEquals(JObject.FromObject(new VisionEngineConfigData()), JObject.FromObject(config)));
        Assert.Equal(source, File.ReadAllText(path));
    }

    [Fact]
    public void StoresUseIndependentExplicitPathsAndPartialFilesKeepDefaults()
    {
        var first = new VisionConfigStore<VisionEngineConfigData>(PathFor("first"));
        var second = new VisionConfigStore<VisionEngineConfigData>(PathFor("second"));
        first.Save(new() { BatchSize = 23 });
        second.Save(new() { BatchSize = 41 });
        Assert.Equal(23, first.Load().BatchSize);
        Assert.Equal(41, second.Load().BatchSize);
        File.WriteAllText(PathFor("first"), "{\"batchSize\":5}");
        Assert.Equal(5, first.Load().BatchSize);
        Assert.Equal(80, first.Load().OcrRichTextThreshold);
        Assert.DoesNotContain(typeof(VisionConfigStore<>).Assembly.GetReferencedAssemblies(), a => a.Name == "AgentCoreProcessor");
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
