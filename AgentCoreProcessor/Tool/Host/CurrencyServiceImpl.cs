using System;
using System.Collections.Generic;
using System.Linq;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host;

/// <summary>
/// ICurrencyService 实现。单账户，委托给 CurrencyStore 进行 JSON 持久化。
/// </summary>
internal class CurrencyServiceImpl : ICurrencyService
{
    private readonly CurrencyStore _store;

    public CurrencyServiceImpl(string dataDirectory)
    {
        _store = new CurrencyStore(dataDirectory);
    }

    public decimal Balance
    {
        get
        {
            var data = _store.Load();
            return data.Balance;
        }
    }

    public CurrencyTransaction Spend(decimal amount, string purpose)
    {
        if (amount <= 0)
            throw new ArgumentException("消费金额必须大于 0", nameof(amount));

        var data = _store.Load();

        if (data.Balance < amount)
            throw new InvalidOperationException($"余额不足：需要 {amount:F1} 币，当前余额 {data.Balance:F1} 币");

        data.Balance -= amount;

        var tx = new CurrencyTransaction
        {
            Type = "spend",
            Amount = amount,
            Reason = purpose,
            Timestamp = DateTime.Now
        };
        data.Transactions.Add(tx);

        _store.Save(data);
        return tx;
    }

    public CurrencyTransaction Grant(decimal amount, string reason)
    {
        if (amount <= 0)
            throw new ArgumentException("拨款金额必须大于 0", nameof(amount));

        var data = _store.Load();
        data.Balance += amount;

        var tx = new CurrencyTransaction
        {
            Type = "grant",
            Amount = amount,
            Reason = reason,
            Timestamp = DateTime.Now
        };
        data.Transactions.Add(tx);

        _store.Save(data);
        return tx;
    }

    public IReadOnlyList<CurrencyTransaction> GetTransactions(int limit = 50)
    {
        var data = _store.Load();
        return data.Transactions.AsEnumerable().Reverse().Take(limit).ToList();
    }
}
