using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.Currency;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "商品购买：使用货币购买虚拟商品（即买即用）")]
public class BuyTool : ITool
{
    private readonly IProductRegistry? _registry;
    private readonly ICurrencyService? _currency;

    public string Name => "currency_buy";
    public string Description => "使用虚拟货币购买商品。购买成功后立即生效，失败不扣款。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("product_id", "要购买的商品 ID（用 currency_shop 查看）", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public BuyTool(IProductRegistry? registry, ICurrencyService? currency)
    {
        _registry = registry;
        _currency = currency;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (_registry == null) return Fail("商品服务不可用");
        if (_currency == null) return Fail("货币服务不可用");

        var productId = inputs[0].Trim();
        var product = _registry.GetById(productId);
        if (product == null)
            return Fail($"未找到商品 '{productId}'。使用 currency_shop 查看可购商品。");

        if (_currency.Balance < product.Price)
            return Fail($"余额不足：{product.Name} 需要 {product.Price:F0} 币，当前余额 {_currency.Balance:F1} 币");

        // 先执行商品效果，成功才扣款
        (bool success, string message) = await product.OnPurchaseAsync();
        if (!success)
            return Fail($"购买失败：{message}");

        // 扣款
        try
        {
            var tx = _currency.Spend(product.Price, product.Name);
            return Ok($"购买成功！{message}\n消费 {product.Price:F0} 币，剩余余额: {_currency.Balance:F1} 币（交易ID：{tx.Id}）");
        }
        catch (Exception ex)
        {
            // 扣款失败（极端情况），告知用户
            return Fail($"扣款异常：{ex.Message}（商品效果已生效，请联系管理员）");
        }
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
