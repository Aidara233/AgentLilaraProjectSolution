using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class AssemblyBoundaryTests
{
    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentLilaraProjectSolution.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("solution directory not found");
    }

    private static Dictionary<string, string[]> References()
    {
        var projects = Directory.GetDirectories(Path.Combine(Repository(), "src"), "Lilara.*");
        return projects.ToDictionary(directory => Path.GetFileName(directory)!, directory =>
        {
            var name = Path.GetFileName(directory);
            var path = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            Assert.True(File.Exists(path), $"missing built assembly: {path}");
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            Assert.Equal(name, metadata.GetString(metadata.GetAssemblyDefinition().Name));
            return metadata.AssemblyReferences.Select(handle =>
                metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToArray();
        }, StringComparer.Ordinal)!;
    }

    [Fact]
    public void ExtractedLibrariesDoNotReferenceHostAndHaveNoAssemblyCycles()
    {
        var references = References();
        Assert.NotEmpty(references);
        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();
        void Visit(string name)
        {
            if (visited.Contains(name)) return;
            Assert.True(visiting.Add(name), $"assembly dependency cycle at {name}");
            Assert.DoesNotContain("AgentCoreProcessor", references[name]);
            foreach (var dependency in references[name].Where(references.ContainsKey)) Visit(dependency);
            visiting.Remove(name);
            visited.Add(name);
        }
        foreach (var name in references.Keys) Visit(name);
    }

    [Theory]
    [InlineData("Lilara.Channel")]
    [InlineData("Lilara.Context")]
    public void PureDomainLibrariesDoNotReferenceOtherApplicationAssemblies(string name)
    {
        var dependencies = References()[name];
        Assert.DoesNotContain(dependencies, dependency => dependency.StartsWith("Lilara.", StringComparison.Ordinal)
            || dependency.StartsWith("AgentLilara.", StringComparison.Ordinal) || dependency == "AgentCoreProcessor");
    }

    [Fact]
    public void PluginProjectReferencesStayWithinSdk()
    {
        // Project files express compile-time references even when unused references are
        // omitted from assembly metadata.
        var root = Repository();
        foreach (var path in Directory.GetFiles(Path.Combine(root, "Plugins"), "Plugin.*.csproj", SearchOption.AllDirectories))
        {
            foreach (var reference in XDocument.Load(path).Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")!.Value.Replace('\\', '/');
                var target = Path.GetFileNameWithoutExtension(include);
                Assert.True(target is "AgentLilara.PluginSDK",
                    $"{Path.GetFileName(path)} references {include}");
            }
        }
    }
}
