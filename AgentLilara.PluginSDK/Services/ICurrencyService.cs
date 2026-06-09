using System;
using System.Collections.Generic;

namespace AgentLilara.PluginSDK.Services;

/// <summary>
/// 货币服务接口。管理 bot 的虚拟货币账户，用于资源申请和消费。
/// </summary>
public interface ICurrencyService
{
    /// <summary>查询指定 person 的余额。账户不存在时返回 0。</summary>
    decimal GetBalance(string personId);

    /// <summary>向账户发放货币（系统操作）。账户不存在时自动创建。</summary>
    CurrencyTransaction Grant(string fromPersonId, string toPersonId, decimal amount, string reason);

    /// <summary>bot 消费货币申请资源。余额不足时抛出 InvalidOperationException。</summary>
    CurrencyTransaction Spend(string personId, decimal amount, string purpose, string resourceType);

    /// <summary>两个账户间转账。付款方余额不足时抛出 InvalidOperationException。</summary>
    CurrencyTransaction Transfer(string fromPersonId, string toPersonId, decimal amount, string reason);

    /// <summary>查询交易记录。可按 person 筛选。</summary>
    IReadOnlyList<CurrencyTransaction> GetTransactions(string? personId = null, int limit = 50);

    /// <summary>获取所有账户信息。</summary>
    IReadOnlyList<CurrencyAccount> GetAllAccounts();
}

// ===== DTO =====

public class CurrencyAccount
{
    public string PersonId { get; set; } = "";
    public decimal Balance { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class CurrencyTransaction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Type { get; set; } = "";          // grant / spend / transfer
    public string FromPersonId { get; set; } = "";
    public string ToPersonId { get; set; } = "";
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
    public string? ResourceType { get; set; }        // spend 时填写
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class CurrencyStoreData
{
    public Dictionary<string, CurrencyAccount> Accounts { get; set; } = new();
    public List<CurrencyTransaction> Transactions { get; set; } = new();
}
