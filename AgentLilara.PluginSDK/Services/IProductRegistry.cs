using System.Collections.Generic;

namespace AgentLilara.PluginSDK.Services;

/// <summary>
/// 商品注册表。插件在 OnInitAsync 中通过此接口注册商品。
/// 参考 IDiceRegistry 模式。
/// </summary>
public interface IProductRegistry
{
    void Register(Product product);
    void Unregister(string productId);
    IReadOnlyList<Product> GetAll();
    Product? GetById(string id);
}
