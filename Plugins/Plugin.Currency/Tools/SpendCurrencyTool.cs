using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.Currency;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "货币消费：bot 使用货币申请或消耗资源")]
public class SpendCurrencyTool : ITool
{
    private readonly ICurrencyService? _currency;

    public string Name => "currency_spend";
    public string Description => "使用虚拟货币消费。余额不足时会失败。供 bot 自身或其他工具调用。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("amount", "消费金额（正数）", 0),
        new("purpose", "消费用途说明", 1)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public SpendCurrencyTool(ICurrencyService? currency) { _currency = currency; }

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (_currency == null) return Task.FromResult(Fail("货币服务不可用"));

        if (!decimal.TryParse(inputs[0], out var amount) || amount <= 0)
            return Task.FromResult(Fail("金额必须是大于 0 的数字"));
        var purpose = inputs[1].Trim();

        try
        {
            var tx = _currency.Spend(amount, purpose);
            return Task.FromResult(Ok($"消费成功！扣除 {amount:F1} 币（{purpose}），剩余余额: {_currency.Balance:F1} 币，交易ID：{tx.Id}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail($"消费失败：{ex.Message}"));
        }
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
