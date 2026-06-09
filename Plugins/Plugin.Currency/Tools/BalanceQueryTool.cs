using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.Currency;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "货币余额查询：查看 bot 当前可用预算")]
public class BalanceQueryTool : ITool
{
    private readonly ICurrencyService? _currency;

    public string Name => "currency_balance";
    public string Description => "查询 bot 当前虚拟货币余额。";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public BalanceQueryTool(ICurrencyService? currency) { _currency = currency; }

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (_currency == null) return Task.FromResult(Fail("货币服务不可用"));
        return Task.FromResult(Ok($"当前余额: {_currency.Balance:F1} 币"));
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
