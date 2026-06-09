using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host;

/// <summary>
/// IProductRegistry 实现。线程安全的内存存储。
/// </summary>
internal class ProductRegistryImpl : IProductRegistry
{
    private readonly ConcurrentDictionary<string, Product> _products = new();

    public void Register(Product product)
    {
        _products[product.Id] = product;
    }

    public void Unregister(string productId)
    {
        _products.TryRemove(productId, out _);
    }

    public IReadOnlyList<Product> GetAll()
    {
        return _products.Values.ToList().AsReadOnly();
    }

    public Product? GetById(string id)
    {
        return _products.TryGetValue(id, out var product) ? product : null;
    }
}
