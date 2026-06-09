using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.Currency;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "商品浏览：查看可购买的虚拟商品货架")]
public class ShopTool : ITool
{
    private readonly IProductRegistry? _registry;
    private readonly ICurrencyService? _currency;

    public string Name => "currency_shop";
    public string Description => "浏览可购买的虚拟商品货架。显示商品名称、价格和描述。";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public ShopTool(IProductRegistry? registry, ICurrencyService? currency)
    {
        _registry = registry;
        _currency = currency;
    }

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (_registry == null) return Task.FromResult(Fail("商品服务不可用"));

        var products = _registry.GetAll();
        if (products.Count == 0)
            return Task.FromResult(Ok("货架空空如也，暂无商品上架。"));

        var groups = products.GroupBy(p => p.Category).OrderBy(g => g.Key);

        var lines = new List<string>();
        var balanceStr = _currency != null ? $"（当前余额: {_currency.Balance:F1} 币）" : "";
        lines.Add($"商品货架 {balanceStr}：");
        lines.Add("");

        foreach (var group in groups)
        {
            lines.Add($"【{group.Key}】");
            foreach (var p in group.OrderBy(p => p.Price))
            {
                lines.Add($"  [{p.Id}] {p.Name} — {p.Price:F0} 币");
                lines.Add($"    {p.Description}");
            }
            lines.Add("");
        }

        lines.Add("使用 currency_buy <商品ID> 购买。");

        return Task.FromResult(Ok(string.Join('\n', lines)));
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
