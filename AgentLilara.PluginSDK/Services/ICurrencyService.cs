using System;
using System.Collections.Generic;

namespace AgentLilara.PluginSDK.Services;

/// <summary>
/// 货币服务接口。bot 内部单账户，用于资源消费和商品购买。
/// </summary>
public interface ICurrencyService
{
    /// <summary>当前余额。</summary>
    decimal Balance { get; }

    /// <summary>消费货币。余额不足时抛出 InvalidOperationException。</summary>
    CurrencyTransaction Spend(decimal amount, string purpose);

    /// <summary>拨款（管理员操作，不暴露为 bot 工具）。</summary>
    CurrencyTransaction Grant(decimal amount, string reason);

    /// <summary>查询交易记录。</summary>
    IReadOnlyList<CurrencyTransaction> GetTransactions(int limit = 50);
}

// ===== DTO =====

public class CurrencyTransaction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Type { get; set; } = "";          // grant / spend
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class CurrencyStoreData
{
    public decimal Balance { get; set; }
    public List<CurrencyTransaction> Transactions { get; set; } = new();
}
