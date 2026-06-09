using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.Currency;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "交易记录查询：查看货币收支流水")]
public class TransactionHistoryTool : ITool
{
    private readonly ICurrencyService? _currency;

    public string Name => "currency_transactions";
    public string Description => "查询货币交易记录。默认返回最近 50 条。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("limit", "返回条数（可选，默认 50，最大 200）", 0, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public TransactionHistoryTool(ICurrencyService? currency) { _currency = currency; }

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (_currency == null) return Task.FromResult(Fail("货币服务不可用"));

        var limit = 50;
        if (inputs.Count > 0 && int.TryParse(inputs[0], out var parsed))
            limit = Math.Clamp(parsed, 1, 200);

        var transactions = _currency.GetTransactions(limit);

        if (transactions.Count == 0)
            return Task.FromResult(Ok("暂无交易记录。"));

        var lines = transactions.Select(t =>
        {
            var prefix = t.Type == "grant" ? "+" : "-";
            return $"[{t.Timestamp:yyyy-MM-dd HH:mm}] {prefix}{t.Amount:F1}币 {t.Reason} (id:{t.Id})";
        });

        return Task.FromResult(Ok($"交易记录（最近 {transactions.Count} 条）：\n" + string.Join('\n', lines)));
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
