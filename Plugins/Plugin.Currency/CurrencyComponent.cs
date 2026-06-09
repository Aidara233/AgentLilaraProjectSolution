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
        Description = "虚拟货币系统：余额查询、发放、消费、转账与交易记录",
        DefaultEnabled = true,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools => _tools;

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _currencyService = context.GetService<ICurrencyService>();
        _tools.Add(new BalanceQueryTool(_currencyService));
        _tools.Add(new GrantCurrencyTool(_currencyService));
        _tools.Add(new SpendCurrencyTool(_currencyService));
        _tools.Add(new TransferCurrencyTool(_currencyService));
        _tools.Add(new TransactionHistoryTool(_currencyService));
        return Task.CompletedTask;
    }

    public override string? BuildPromptSection(LoopInfo caller)
    {
        if (_currencyService == null) return null;
        var accounts = _currencyService.GetAllAccounts();
        if (accounts.Count == 0) return null;
        var lines = accounts.Select(a => $"- {a.PersonId}: {a.Balance:F1} 币");
        return "当前货币账户：\n" + string.Join('\n', lines);
    }
}
