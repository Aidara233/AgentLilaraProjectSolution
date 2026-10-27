using AgentCoreProcessor.Tool;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class ToolDescriptionRendererTests
{
    private sealed class Tool(string name, string description, params ToolParameter[] parameters) : ITool
    {
        public string Name => name;
        public string Description => description;
        public IReadOnlyList<ToolParameter> Parameters => parameters;
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public void EmptySequenceProducesEmptyText() => Assert.Equal("", ToolDescriptionRenderer.Render(Array.Empty<ITool>()));

    [Fact]
    public void PreservesOrderParameterIndicesExamplesAndPlatformNewlines()
    {
        var tools = new ITool[]
        {
            new Tool("first", "描述\n第二行", new("later", "", 7), new("earlier", "", 2, false)),
            new Tool("second", "无参数")
        };
        var expected = string.Join(Environment.NewLine, new[]
        {
            "工具1：first", "描述：描述\n第二行", "参数：inputs[7] = later, inputs[2] = earlier",
            "示例：{\"tool\":\"first\",\"inputs\":[\"(later)\",\"(earlier)\"]}<over>", "",
            "工具2：second", "描述：无参数", "示例：{\"tool\":\"second\",\"inputs\":[]}<over>"
        });
        Assert.Equal(expected, ToolDescriptionRenderer.Render(tools));
    }

    [Fact]
    public void RegistryFiltersBeforeRenderingAdditionalTools()
    {
        var unique = "renderer-test-" + Guid.NewGuid().ToString("N");
        var tool = new Tool(unique, "description");
        Assert.True(ToolRegistry.Register(tool));
        try
        {
            var additional = new Tool("additional", "extra");
            Assert.Equal(ToolDescriptionRenderer.Render(new ITool[] { tool, additional }),
                ToolRegistry.GenerateDescriptions(t => t.Name == unique, new[] { additional }));
        }
        finally { ToolRegistry.Unregister(unique); }
    }
}
