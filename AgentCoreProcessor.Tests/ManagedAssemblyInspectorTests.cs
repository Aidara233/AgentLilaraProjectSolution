using AgentCoreProcessor.Tool.Host;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class ManagedAssemblyInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pe-inspection-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RecognizesRealSdkHostAndPluginHostWithoutLoadingCandidates()
    {
        Assert.True(ManagedAssemblyInspector.IsManagedAssembly(typeof(ITool).Assembly.Location));
        Assert.True(ManagedAssemblyInspector.IsManagedAssembly(typeof(PluginLoader).Assembly.Location));
        Assert.True(ManagedAssemblyInspector.IsManagedAssembly(typeof(ManagedAssemblyInspector).Assembly.Location));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(511)]
    [InlineData(4096)]
    public void RejectsEmptyShortAndRandomFiles(int size)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "candidate.dll");
        var bytes = new byte[size];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        Assert.False(ManagedAssemblyInspector.IsManagedAssembly(path));
        // The inspector disposes the input even when metadata parsing fails.
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void RejectsTruncatedRealPeAndMissingFile()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "truncated.dll");
        var assembly = File.ReadAllBytes(typeof(ITool).Assembly.Location);
        File.WriteAllBytes(path, assembly[..512]);
        Assert.False(ManagedAssemblyInspector.IsManagedAssembly(path));
        Assert.False(ManagedAssemblyInspector.IsManagedAssembly(Path.Combine(_root, "missing.dll")));
        Assert.False(ManagedAssemblyInspector.IsManagedAssembly(_root));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
