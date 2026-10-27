using System.Reflection;
using System.Runtime.Loader;
using AgentLilara.PluginSDK;
using FileToolKit.Shared;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class FileToolBaseCompatibilityTests
{
    [Fact]
    public void LegacyAssemblyForwardsBaseToSingleSdkDefinition()
    {
        Assert.Same(typeof(ITool).Assembly, typeof(FileToolBase).Assembly);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentLilaraProjectSolution.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var path = Path.Combine(directory.FullName, "Plugins", "FileToolKit.Shared", "bin", configuration, "net8.0", "FileToolKit.Shared.dll");
        Assert.True(File.Exists(path), path);
        var context = new CompatibilityContext();
        try
        {
            var legacy = context.LoadFromAssemblyPath(path);
            Assert.Contains(typeof(FileToolBase), legacy.GetForwardedTypes());
            Assert.Same(typeof(FileToolBase), legacy.GetType("FileToolKit.Shared.FileToolBase", throwOnError: true));
            Assert.Empty(legacy.GetTypes());
        }
        finally { context.Unload(); }
    }

    private sealed class CompatibilityContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) =>
            name.Name == typeof(ITool).Assembly.GetName().Name ? typeof(ITool).Assembly : null;
    }
}
