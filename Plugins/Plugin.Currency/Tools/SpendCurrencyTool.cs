using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.Currency;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "货币消费：bot 使用货币申请资源")]
public class SpendCurrencyTool : ITool
{
    private readonly ICurrencyService? _currency;

    public string Name => "currency_spend";
    public string Description => "使用虚拟货币消费（申请资源）。余额不足时会失败。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("person_id", "消费的用户 ID", 0),
        new("amount", "消费金额（正数）", 1),
        new("purpose", "消费用途说明", 2),
        new("resource_type", "资源类型（如 api_call / compute / storage 等）", 3)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public SpendCurrencyTool(ICurrencyService? currency) { _currency = currency; }

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (_currency == null) return Task.FromResult(Fail("货币服务不可用"));

        var personId = inputs[0].Trim();
        if (!decimal.TryParse(inputs[1], out var amount) || amount <= 0)
            return Task.FromResult(Fail("金额必须是大于 0 的数字"));
        var purpose = inputs[2].Trim();
        var resourceType = inputs[3].Trim();

        try
        {
            var tx = _currency.Spend(personId, amount, purpose, resourceType);
            var balance = _currency.GetBalance(personId);
            return Task.FromResult(Ok($"消费成功！{personId} 消费 {amount:F1} 币（{resourceType}: {purpose}），剩余余额: {balance:F1} 币，交易ID：{tx.Id}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail($"消费失败：{ex.Message}"));
        }
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
