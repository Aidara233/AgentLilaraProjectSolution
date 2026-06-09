using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.Currency;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "货币余额查询：查看指定用户或所有账户的余额")]
public class BalanceQueryTool : ITool
{
    private readonly ICurrencyService? _currency;

    public string Name => "currency_balance";
    public string Description => "查询虚拟货币余额。不指定 person 时返回所有账户的余额概览。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("person_id", "要查询的用户 ID（可选，不填则列出全部）", 0, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public BalanceQueryTool(ICurrencyService? currency) { _currency = currency; }

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (_currency == null) return Task.FromResult(Fail("货币服务不可用"));

        var personId = inputs.Count > 0 && !string.IsNullOrWhiteSpace(inputs[0]) ? inputs[0].Trim() : null;

        if (personId != null)
        {
            var balance = _currency.GetBalance(personId);
            return Task.FromResult(Ok($"账户 {personId} 余额: {balance:F1} 币"));
        }
        else
        {
            var accounts = _currency.GetAllAccounts();
            if (accounts.Count == 0)
                return Task.FromResult(Ok("暂无任何货币账户。使用 currency_grant 创建初始账户。"));
            var lines = accounts.Select(a => $"- {a.PersonId}: {a.Balance:F1} 币");
            return Task.FromResult(Ok("货币账户一览：\n" + string.Join('\n', lines)));
        }
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
