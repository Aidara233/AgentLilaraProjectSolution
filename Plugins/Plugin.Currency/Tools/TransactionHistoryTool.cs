using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.Currency;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "交易记录查询：查看货币交易流水")]
public class TransactionHistoryTool : ITool
{
    private readonly ICurrencyService? _currency;

    public string Name => "currency_transactions";
    public string Description => "查询货币交易记录。可按用户筛选，默认返回最近 50 条。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("person_id", "按用户 ID 筛选（可选）", 0, false),
        new("limit", "返回条数（可选，默认 50，最大 200）", 1, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public TransactionHistoryTool(ICurrencyService? currency) { _currency = currency; }

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (_currency == null) return Task.FromResult(Fail("货币服务不可用"));

        var personId = inputs.Count > 0 && !string.IsNullOrWhiteSpace(inputs[0]) ? inputs[0].Trim() : null;
        var limit = 50;
        if (inputs.Count > 1 && int.TryParse(inputs[1], out var parsed))
            limit = Math.Clamp(parsed, 1, 200);

        var transactions = _currency.GetTransactions(personId, limit);

        if (transactions.Count == 0)
            return Task.FromResult(Ok("暂无交易记录。"));

        var lines = transactions.Select(t =>
        {
            var arrow = t.Type switch
            {
                "grant" => $"system → {t.ToPersonId}",
                "spend" => $"{t.FromPersonId} → system",
                "transfer" => $"{t.FromPersonId} → {t.ToPersonId}",
                _ => $"{t.FromPersonId} → {t.ToPersonId}"
            };
            var extra = t.Type == "spend" && t.ResourceType != null ? $" [{t.ResourceType}]" : "";
            return $"[{t.Timestamp:yyyy-MM-dd HH:mm}] {t.Type}{extra} {arrow} {t.Amount:F1}币 {t.Reason} (id:{t.Id})";
        });

        var header = personId != null
            ? $"用户 {personId} 的交易记录（最近 {transactions.Count} 条）：\n"
            : $"全局交易记录（最近 {transactions.Count} 条）：\n";
        return Task.FromResult(Ok(header + string.Join('\n', lines)));
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
