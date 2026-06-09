using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host;

/// <summary>
/// 货币数据 JSON 持久化存储。线程安全，原子写入。
/// </summary>
internal class CurrencyStore
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public CurrencyStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "currency.json");
    }

    public CurrencyStoreData Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
                return new CurrencyStoreData();
            try
            {
                var json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<CurrencyStoreData>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new CurrencyStoreData();
                foreach (var tx in data.Transactions)
                    tx.Timestamp = DateTime.SpecifyKind(tx.Timestamp, DateTimeKind.Local);
                return data;
            }
            catch
            {
                return new CurrencyStoreData();
            }
        }
    }

    public void Save(CurrencyStoreData data)
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _filePath, overwrite: true);
        }
    }
}
