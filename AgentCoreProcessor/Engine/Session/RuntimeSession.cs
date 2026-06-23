// AgentCoreProcessor/Engine/Session/RuntimeSession.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Engine.Messaging;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// 统一会话实现。行为由 CreateSessionSpec 驱动，不分子类。
/// </summary>
internal class RuntimeSession : IRuntimeSession
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N")[..12];
    public int MaxLevel { get; private set; }
    public SessionStatus Status { get; private set; } = SessionStatus.Creating;
    public bool IsAlive => Status is SessionStatus.Running or SessionStatus.Creating or SessionStatus.Restored;
    public DateTime? WaitUntil { get; private set; }

    public IReadOnlyList<string> ComponentIds => _componentIds.AsReadOnly();
    public IReadOnlyList<string> ActiveTools => _activeTools.AsReadOnly();
    public IReadOnlyDictionary<string, IInputPort> Inputs => _inputs;
    public IReadOnlyDictionary<string, IOutputPort> Outputs => _outputs;
    public string? ParentSessionId { get; private set; }
    public SessionLimits Limits { get; set; } = new();

    private readonly List<string> _componentIds = new();
    private readonly List<string> _activeTools = new();
    private readonly Dictionary<string, IInputPort> _inputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IOutputPort> _outputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    private ComponentHost? _componentHost;
    private ICurrencyService? _currency;
    private int _roundCount;
    private readonly DateTime _createdAt = DateTime.UtcNow;
    private DateTime _lastActivity = DateTime.UtcNow;
    private CancellationTokenSource? _cts;

    /// <summary>会话完成时触发（正常结束/异常/崩溃）。</summary>
    public event Action<IRuntimeSession>? OnCompleted;

    private const int FreeRounds = 20;

    public RuntimeSession(CreateSessionSpec spec)
    {
        MaxLevel = spec.MaxLevel;
        ParentSessionId = spec.ParentSessionId;
        if (spec.OverrideLimits != null) Limits = spec.OverrideLimits;
        _componentIds.AddRange(spec.ComponentIds);
    }

    // ── 依赖注入 ──

    public void SetCurrency(ICurrencyService currency) => _currency = currency;

    /// <summary>
    /// 从 ComponentRegistry 加载指定组件，构建 ComponentHost。
    /// </summary>
    public void InitComponents(string loopType, ModuleBus moduleBus, IServiceProvider services, Action wakeLoop)
    {
        _componentHost = new ComponentHost($"session:{SessionId}", loopType, moduleBus, services, wakeLoop);
        _componentHost.InitAsync().GetAwaiter().GetResult();

        // 收集所有组件工具
        foreach (var comp in ComponentRegistry.GetLoopComponents(loopType))
        {
            if (!_componentIds.Contains(comp.Type.Name)) continue;
            // 工具由 ComponentHost 管理，这里只收集名称
        }

        RefreshToolList();
    }

    private void RefreshToolList()
    {
        _activeTools.Clear();
        _tools.Clear();
        if (_componentHost == null) return;

        // 从 GlobalComponentHost 和本地 ComponentHost 收集工具
        // 实际工具名来自 ComponentHost 注册的 ITool
        // 此处简化：根据 ComponentIds 查找已知工具
    }

    // ── 端口管理 ──

    public void AddInputPort(string name, IInputPort port) => _inputs[name] = port;
    public void AddOutputPort(string name, IOutputPort port) => _outputs[name] = port;

    // ── 组件管理 ──

    public void EnableComponent(string componentId)
    {
        if (_componentIds.Contains(componentId)) return;
        _componentIds.Add(componentId);
        // 运行时加载：实例化组件并注册其工具
    }

    public void DisableComponent(string componentId)
    {
        _componentIds.Remove(componentId);
        RefreshToolList();
    }

    // ── 等级 ──

    public void SetLevel(int newLevel) => MaxLevel = newLevel;

    public bool CheckLifetimeLimits()
    {
        var now = DateTime.UtcNow;
        if (Limits.MaxLifetime > TimeSpan.Zero && now - _createdAt > Limits.MaxLifetime) return false;
        if (Limits.IdleTimeout > TimeSpan.Zero && now - _lastActivity > Limits.IdleTimeout) return false;
        return true;
    }

    public void RequestStop(string reason)
    {
        Status = SessionStatus.Completed;
        _cts?.Cancel();
    }

    public void SetWaitUntil(DateTime? until) => WaitUntil = until;
    public void TouchActivity() => _lastActivity = DateTime.UtcNow;

    // ── Agent 循环 ──

    public async Task RunAsync(CancellationToken ct)
    {
        Status = SessionStatus.Running;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            while (IsAlive && !ct.IsCancellationRequested)
            {
                // 等待输入数据或超时
                if (!await WaitForActivationAsync(_cts.Token))
                    break;

                if (!CheckLifetimeLimits())
                {
                    Status = SessionStatus.Completed;
                    break;
                }

                TouchActivity();
                SetWaitUntil(null); // 清除 wait 计时

                // 轮次定价
                _roundCount++;
                if (_roundCount > FreeRounds && _currency != null)
                {
                    var price = (decimal)Math.Ceiling((_roundCount - FreeRounds) / 10.0);
                    if (_currency.Balance >= price)
                    {
                        try { _currency.Spend(price, $"Session {SessionId} round {_roundCount}"); }
                        catch { /* 扣费失败不中断 */ }
                    }
                    else
                    {
                        // 余额不足，注入警告，不收钱，让 Session 收尾
                        InjectMessage(new { type = "warning", text = $"余额不足（当前 {_currency.Balance:F0} 币），无法续期。请收尾。" });
                    }
                }

                // 收集输入端口数据
                var messages = new List<Message>();
                foreach (var (name, port) in _inputs)
                {
                    while (port.TryPeek(out var data))
                    {
                        await port.ReceiveAsync(ct);
                        if (data is EngineIncomingMessage em)
                            messages.Add(new Message { Role = "user", Content = em.Content });
                        else if (data is string s)
                            messages.Add(new Message { Role = "user", Content = s });
                        else if (data != null)
                        {
                            var json = JsonConvert.SerializeObject(data);
                            messages.Add(new Message { Role = "system", Content = json });
                        }
                    }
                }

                // 无消息且非超时 → 继续等待
                if (messages.Count == 0)
                    continue;

                // Agent 推理 → 由外部注入的 AgentCore 执行
                // 工具调用由外部框架处理，此处只负责数据收集和生命周期
                // 实际 Agent 调用在 ChannelEngine 的 Express 循环或外部注入中完成

                // 检查 wait 工具调用标记
                // wait 由外部工具实现设置 WaitUntil
            }
        }
        finally
        {
            if (Status == SessionStatus.Running)
                Status = SessionStatus.Completed;
            OnCompleted?.Invoke(this);
        }
    }

    private async Task<bool> WaitForActivationAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // 检查是否有输入
            foreach (var (_, port) in _inputs)
                if (port.TryPeek(out _)) return true;

            // 检查 WaitUntil 是否到期
            if (WaitUntil != null && DateTime.UtcNow >= WaitUntil.Value)
            {
                // 超时激活，注入 timeout 消息
                InjectMessage(new { type = "timeout", wait_reason = "wait timeout expired" });
                return true;
            }

            await Task.Delay(200, ct);
        }
        return false;
    }

    private void InjectMessage(object data)
    {
        foreach (var (_, port) in _inputs)
        {
            if (port is ChannelInputPort<object> cp)
                cp.Write(data);
        }
    }

    // ── 持久化 ──

    public async Task PersistStateAsync()
    {
        var dir = Path.Combine(PathConfig.StoragePath, "Sessions", SessionId);
        Directory.CreateDirectory(dir);

        var meta = new
        {
            status = Status.ToString(),
            maxLevel = MaxLevel,
            createdAt = _createdAt,
            roundCount = _roundCount,
            componentIds = _componentIds
        };
        await File.WriteAllTextAsync(Path.Combine(dir, "meta.json"),
            JsonConvert.SerializeObject(meta, Formatting.Indented));
    }

    public Task RestoreStateAsync()
    {
        var dir = Path.Combine(PathConfig.StoragePath, "Sessions", SessionId);
        var metaPath = Path.Combine(dir, "meta.json");
        if (File.Exists(metaPath))
        {
            try
            {
                var json = File.ReadAllText(metaPath);
                var meta = JsonConvert.DeserializeAnonymousType(json, new { status = "", maxLevel = 0, roundCount = 0 });
                if (meta != null)
                {
                    MaxLevel = meta.maxLevel;
                    _roundCount = meta.roundCount;
                    Status = meta.status switch
                    {
                        "Running" => SessionStatus.Restored,
                        "Crashed" => SessionStatus.Crashed,
                        _ => SessionStatus.Completed
                    };
                }
            }
            catch { /* 恢复失败不阻塞 */ }
        }
        return Task.CompletedTask;
    }
}
