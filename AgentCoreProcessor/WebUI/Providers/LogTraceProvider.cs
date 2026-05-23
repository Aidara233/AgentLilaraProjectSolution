using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Providers;

[WebUIProvider(BuiltIn = true)]
internal class LogTraceProvider : IWebUIProvider
{
    public string Id => "log-trace";
    public string DisplayName => "信号追踪";

    public IReadOnlyList<PageDefinition> Pages { get; } = new List<PageDefinition>
    {
        new()
        {
            Route = "logs/trace",
            Meta = new PageMeta { Title = "信号追踪", Icon = "bi-diagram-3", Group = "日志", Order = 10 },
            Cards = new List<CardDefinition>(),
            DataSources = new List<DataSourceDefinition>()
        }
    };
}
