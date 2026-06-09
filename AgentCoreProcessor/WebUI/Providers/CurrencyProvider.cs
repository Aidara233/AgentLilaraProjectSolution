using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentLilara.PluginSDK.Services;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Providers;

internal class CurrencyProvider : IWebUIProvider
{
    private readonly MasterEngine _engine;

    public string Id => "currency";
    public string DisplayName => "货币管理";
    public IReadOnlyList<PageDefinition> Pages { get; }

    public CurrencyProvider(MasterEngine engine)
    {
        _engine = engine;

        Pages = new List<PageDefinition>
        {
            new()
            {
                Route = "currency",
                Meta = new PageMeta
                {
                    Title = "货币管理",
                    Icon = "bi-coin",
                    ShowInNav = true,
                    Group = "调试",
                    Order = 115,
                },
                Cards = new List<CardDefinition>
                {
                    BuildStatusCard(),
                    BuildGrantCard(),
                    BuildTransactionsCard(),
                },
                DataSources = new List<DataSourceDefinition>
                {
                    new() { Id = "currency-status", Source = new CurrencyStatusSource(_engine) },
                    new() { Id = "currency-grant", Source = new CurrencyGrantSource(_engine) },
                    new() { Id = "currency-transactions", Source = new CurrencyTransactionsSource(_engine) },
                },
            },
        };
    }

    private static CardDefinition BuildStatusCard()
    {
        return new CardDefinition
        {
            Id = "currency-status",
            Type = CardType.Status,
            Title = "账户状态",
            DataSourceId = "currency-status",
            Schema = new StatusSchema
            {
                Fields = new List<StatusField>
                {
                    new() { Field = "balance", Label = "当前余额" },
                    new() { Field = "totalGranted", Label = "累计拨款" },
                    new() { Field = "totalSpent", Label = "累计消费" },
                    new() { Field = "lastUpdated", Label = "最近交易", Type = StatusFieldType.DateTime },
                },
            },
            Layout = new CardLayout { PreferredCols = 12 },
        };
    }

    private static CardDefinition BuildGrantCard()
    {
        return new CardDefinition
        {
            Id = "currency-grant",
            Type = CardType.Form,
            Title = "拨款",
            DataSourceId = "currency-grant",
            Schema = new FormSchema
            {
                Fields = new List<FormField>
                {
                    new()
                    {
                        Field = "amount",
                        Label = "金额",
                        Type = FormFieldType.Number,
                        Required = true,
                        Description = "拨款金额（正数）",
                    },
                    new()
                    {
                        Field = "reason",
                        Label = "原因",
                        Type = FormFieldType.Text,
                        Required = true,
                        Description = "拨款原因说明",
                    },
                },
                ShowSubmit = true,
                ShowReset = true,
            },
            Layout = new CardLayout { PreferredCols = 4 },
        };
    }

    private static CardDefinition BuildTransactionsCard()
    {
        return new CardDefinition
        {
            Id = "currency-transactions",
            Type = CardType.Table,
            Title = "交易记录",
            DataSourceId = "currency-transactions",
            Schema = new TableSchema
            {
                Columns = new List<ColumnDef>
                {
                    new() { Field = "timestamp", Header = "时间", Format = ColumnFormat.DateTime },
                    new() { Field = "type", Header = "类型", Format = ColumnFormat.Badge },
                    new() { Field = "amount", Header = "金额" },
                    new() { Field = "reason", Header = "说明" },
                    new() { Field = "id", Header = "交易ID" },
                },
                Searchable = false,
                Paginated = true,
                DefaultPageSize = 20,
            },
            Layout = new CardLayout { PreferredCols = 8 },
        };
    }
}

// ---- Helpers ----

internal static class CurrencySourceHelper
{
    public static ICurrencyService? GetCurrency(MasterEngine engine)
        => engine.ComponentServices.GetService(typeof(ICurrencyService)) as ICurrencyService;

    public static ICurrencyService RequireCurrency(MasterEngine engine)
        => GetCurrency(engine) ?? throw new InvalidOperationException("货币服务未就绪");
}

// ---- Data Sources ----

internal class CurrencyStatusSource : IDataSource
{
    private readonly MasterEngine _engine;

    public CurrencyStatusSource(MasterEngine engine) { _engine = engine; }

    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var currency = CurrencySourceHelper.RequireCurrency(_engine);
        var transactions = currency.GetTransactions(1000);
        var totalGranted = transactions.Where(t => t.Type == "grant").Sum(t => t.Amount);
        var totalSpent = transactions.Where(t => t.Type == "spend").Sum(t => t.Amount);
        var lastTx = transactions.FirstOrDefault();

        var data = new JsonObject
        {
            ["balance"] = $"{currency.Balance:F1} 币",
            ["totalGranted"] = $"{totalGranted:F1} 币",
            ["totalSpent"] = $"{totalSpent:F1} 币",
            ["lastUpdated"] = lastTx?.Timestamp.ToString("yyyy-MM-dd HH:mm:ss") ?? "无",
        };

        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false, Message = "不支持操作" });
}

internal class CurrencyGrantSource : IDataSource
{
    private readonly MasterEngine _engine;

    public CurrencyGrantSource(MasterEngine engine) { _engine = engine; }

    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
        => Task.FromResult(new DataResult { Data = new JsonObject() });

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (action != "save")
            return Task.FromResult(new ActionResult { Success = false, Message = "不支持的操作" });

        if (data is not JsonObject obj)
            return Task.FromResult(new ActionResult { Success = false, Message = "无效数据" });

        var amountStr = obj["amount"]?.ToString();
        var reason = obj["reason"]?.ToString() ?? "";

        if (!decimal.TryParse(amountStr, out var amount) || amount <= 0)
            return Task.FromResult(new ActionResult { Success = false, Message = "金额必须是大于 0 的数字" });

        if (string.IsNullOrWhiteSpace(reason))
            return Task.FromResult(new ActionResult { Success = false, Message = "请填写拨款原因" });

        try
        {
            var currency = CurrencySourceHelper.RequireCurrency(_engine);
            currency.Grant(amount, reason);
            return Task.FromResult(new ActionResult { Success = true, Message = $"拨款成功！发放 {amount:F1} 币，当前余额 {currency.Balance:F1} 币" });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ActionResult { Success = false, Message = $"拨款失败：{ex.Message}" });
        }
    }
}

internal class CurrencyTransactionsSource : IDataSource
{
    private readonly MasterEngine _engine;

    public CurrencyTransactionsSource(MasterEngine engine) { _engine = engine; }

    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var currency = CurrencySourceHelper.RequireCurrency(_engine);
        var limit = query?.PageSize > 0 ? query.PageSize.Value : 500;
        var transactions = currency.GetTransactions(limit);
        var rows = new JsonArray();

        foreach (var t in transactions)
        {
            var prefix = t.Type == "grant" ? "+" : "-";
            rows.Add(new JsonObject
            {
                ["id"] = t.Id,
                ["timestamp"] = t.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                ["type"] = t.Type == "grant" ? "拨款" : "消费",
                ["amount"] = $"{prefix}{t.Amount:F1}",
                ["reason"] = t.Reason,
            });
        }

        return Task.FromResult(new DataResult { Data = rows, TotalCount = rows.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = false, Message = "不支持操作" });
}
