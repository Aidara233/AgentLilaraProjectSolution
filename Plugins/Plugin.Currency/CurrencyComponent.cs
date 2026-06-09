using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.Currency;

[Component(Name = "currency", Scope = ComponentScope.Global)]
public class CurrencyComponent : GlobalComponentBase
{
    private List<ITool> _tools = new();
    private ICurrencyService? _currencyService;

    public override ComponentMeta Meta => new()
    {
        Name = "currency",
        Description = "虚拟货币系统：余额查询、商品浏览/购买、消费扣款、交易记录",
        DefaultEnabled = true,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools => _tools;

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _currencyService = context.GetService<ICurrencyService>();
        var productRegistry = context.GetService<IProductRegistry>();

        _tools.Add(new BalanceQueryTool(_currencyService));
        _tools.Add(new ShopTool(productRegistry, _currencyService));
        _tools.Add(new BuyTool(productRegistry, _currencyService));
        _tools.Add(new SpendCurrencyTool(_currencyService));
        _tools.Add(new TransactionHistoryTool(_currencyService));

        return Task.CompletedTask;
    }

    public override string? BuildPromptSection(LoopInfo caller)
    {
        if (_currencyService == null) return null;
        return $"当前余额: {_currencyService.Balance:F1} 币。使用 currency_shop 查看可购商品，currency_buy <id> 购买，currency_spend <金额> <用途> 通用消费。";
    }
}
