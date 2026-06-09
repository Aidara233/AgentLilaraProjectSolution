using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.Currency;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "货币转账：在两个账户间转移货币")]
public class TransferCurrencyTool : ITool
{
    private readonly ICurrencyService? _currency;

    public string Name => "currency_transfer";
    public string Description => "在两个用户的货币账户间转账。付款方余额不足时会失败。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("from_person", "付款方用户 ID", 0),
        new("to_person", "收款方用户 ID", 1),
        new("amount", "转账金额（正数）", 2),
        new("reason", "转账原因", 3)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public TransferCurrencyTool(ICurrencyService? currency) { _currency = currency; }

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (_currency == null) return Task.FromResult(Fail("货币服务不可用"));

        var fromPerson = inputs[0].Trim();
        var toPerson = inputs[1].Trim();
        if (!decimal.TryParse(inputs[2], out var amount) || amount <= 0)
            return Task.FromResult(Fail("金额必须是大于 0 的数字"));
        var reason = inputs[3].Trim();

        try
        {
            var tx = _currency.Transfer(fromPerson, toPerson, amount, reason);
            return Task.FromResult(Ok($"转账成功！{fromPerson} → {toPerson}，金额 {amount:F1} 币（{reason}），交易ID：{tx.Id}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail($"转账失败：{ex.Message}"));
        }
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
