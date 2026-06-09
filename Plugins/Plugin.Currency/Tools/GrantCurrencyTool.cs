using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.Currency;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "货币发放：向指定账户发放虚拟货币")]
public class GrantCurrencyTool : ITool
{
    private readonly ICurrencyService? _currency;

    public string Name => "currency_grant";
    public string Description => "向指定用户的账户发放虚拟货币（系统操作）。账户不存在时自动创建。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("target_person", "接收货币的用户 ID", 0),
        new("amount", "发放金额（正数）", 1),
        new("reason", "发放原因", 2)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public GrantCurrencyTool(ICurrencyService? currency) { _currency = currency; }

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (_currency == null) return Task.FromResult(Fail("货币服务不可用"));

        var targetPerson = inputs[0].Trim();
        if (!decimal.TryParse(inputs[1], out var amount) || amount <= 0)
            return Task.FromResult(Fail("金额必须是大于 0 的数字"));
        var reason = inputs[2].Trim();

        try
        {
            var tx = _currency.Grant("system", targetPerson, amount, reason);
            return Task.FromResult(Ok($"发放成功！向 {targetPerson} 发放 {amount:F1} 币（原因：{reason}），交易ID：{tx.Id}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail($"发放失败：{ex.Message}"));
        }
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
