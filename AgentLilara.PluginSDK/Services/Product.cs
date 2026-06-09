using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services;

/// <summary>
/// 可购买商品的抽象基类。插件继承此类实现自定义商品效果。
/// </summary>
public abstract class Product
{
    /// <summary>唯一标识。</summary>
    public string Id { get; init; } = "";

    /// <summary>显示名称。</summary>
    public string Name { get; init; } = "";

    /// <summary>商品描述，展示在货架上。</summary>
    public string Description { get; init; } = "";

    /// <summary>价格（币）。</summary>
    public decimal Price { get; init; }

    /// <summary>分类标签，货架按此分组。</summary>
    public string Category { get; init; } = "";

    /// <summary>
    /// 购买时调用。返回 (success, message)。
    /// 仅 success=true 时才扣款；失败原因通过 message 告知用户。
    /// </summary>
    public virtual Task<(bool Success, string Message)> OnPurchaseAsync()
        => Task.FromResult((true, "购买成功"));
}
