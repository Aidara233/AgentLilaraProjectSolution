using System;
using System.Collections.Generic;
using System.Linq;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host;

/// <summary>
/// ICurrencyService 实现。委托给 CurrencyStore 进行 JSON 持久化。
/// </summary>
internal class CurrencyServiceImpl : ICurrencyService
{
    private readonly CurrencyStore _store;

    public CurrencyServiceImpl(string dataDirectory)
    {
        _store = new CurrencyStore(dataDirectory);
    }

    public decimal GetBalance(string personId)
    {
        var data = _store.Load();
        return data.Accounts.TryGetValue(personId, out var account) ? account.Balance : 0;
    }

    public CurrencyTransaction Grant(string fromPersonId, string toPersonId, decimal amount, string reason)
    {
        if (amount <= 0)
            throw new ArgumentException("发放金额必须大于 0", nameof(amount));

        var data = _store.Load();
        var now = DateTime.Now;

        if (!data.Accounts.ContainsKey(toPersonId))
        {
            data.Accounts[toPersonId] = new CurrencyAccount
            {
                PersonId = toPersonId,
                Balance = 0,
                CreatedAt = now
            };
        }

        data.Accounts[toPersonId].Balance += amount;
        data.Accounts[toPersonId].UpdatedAt = now;

        var tx = new CurrencyTransaction
        {
            Type = "grant",
            FromPersonId = fromPersonId,
            ToPersonId = toPersonId,
            Amount = amount,
            Reason = reason,
            Timestamp = now
        };
        data.Transactions.Add(tx);

        _store.Save(data);
        return tx;
    }

    public CurrencyTransaction Spend(string personId, decimal amount, string purpose, string resourceType)
    {
        if (amount <= 0)
            throw new ArgumentException("消费金额必须大于 0", nameof(amount));

        var data = _store.Load();
        var now = DateTime.Now;

        if (!data.Accounts.TryGetValue(personId, out var account))
            throw new InvalidOperationException($"账户 {personId} 不存在，无法消费");

        if (account.Balance < amount)
            throw new InvalidOperationException($"余额不足：需要 {amount}，当前余额 {account.Balance}");

        account.Balance -= amount;
        account.UpdatedAt = now;

        var tx = new CurrencyTransaction
        {
            Type = "spend",
            FromPersonId = personId,
            ToPersonId = "system",
            Amount = amount,
            Reason = purpose,
            ResourceType = resourceType,
            Timestamp = now
        };
        data.Transactions.Add(tx);

        _store.Save(data);
        return tx;
    }

    public CurrencyTransaction Transfer(string fromPersonId, string toPersonId, decimal amount, string reason)
    {
        if (amount <= 0)
            throw new ArgumentException("转账金额必须大于 0", nameof(amount));
        if (fromPersonId == toPersonId)
            throw new ArgumentException("不能向自己转账");

        var data = _store.Load();
        var now = DateTime.Now;

        if (!data.Accounts.TryGetValue(fromPersonId, out var fromAccount))
            throw new InvalidOperationException($"付款方 {fromPersonId} 账户不存在");

        if (fromAccount.Balance < amount)
            throw new InvalidOperationException($"余额不足：需要 {amount}，当前余额 {fromAccount.Balance}");

        if (!data.Accounts.ContainsKey(toPersonId))
        {
            data.Accounts[toPersonId] = new CurrencyAccount
            {
                PersonId = toPersonId,
                Balance = 0,
                CreatedAt = now
            };
        }

        fromAccount.Balance -= amount;
        fromAccount.UpdatedAt = now;
        data.Accounts[toPersonId].Balance += amount;
        data.Accounts[toPersonId].UpdatedAt = now;

        var tx = new CurrencyTransaction
        {
            Type = "transfer",
            FromPersonId = fromPersonId,
            ToPersonId = toPersonId,
            Amount = amount,
            Reason = reason,
            Timestamp = now
        };
        data.Transactions.Add(tx);

        _store.Save(data);
        return tx;
    }

    public IReadOnlyList<CurrencyTransaction> GetTransactions(string? personId = null, int limit = 50)
    {
        var data = _store.Load();
        var query = data.Transactions.AsEnumerable().Reverse();
        if (personId != null)
            query = query.Where(t => t.FromPersonId == personId || t.ToPersonId == personId);
        return query.Take(limit).ToList();
    }

    public IReadOnlyList<CurrencyAccount> GetAllAccounts()
    {
        var data = _store.Load();
        return data.Accounts.Values.OrderByDescending(a => a.Balance).ToList();
    }
}
