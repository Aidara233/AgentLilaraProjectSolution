using System.Text.Json;
using AgentCoreProcessor.Tool.Host;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool.Core;

/// <summary>模型图编辑入口；图校验与文件写入由宿主统一处理。</summary>
internal sealed class WireTool : ITool
{
    private readonly IWireAccess _access;
    public WireTool(string name, IWireAccess access) { Name = name; _access = access; }
    public string Name { get; }
    public string Description => Name switch
    {
        "wire_describe" => "列出可用图、内建节点端口、参数示例和挂载表；传 class_id 可读取完整图源码。",
        "wire_validate" => "用生产图加载器校验图 JSON，不执行图、不保存。每张图必须恰好一个激活源，所有必填输入口必须连接。",
        "wire_apply" => "校验并保存完整图 JSON，创建或替换图类；返回审计版本。静态挂载需重载挂载表；已有订阅需删除重建或重启。",
        _ => "校验并原子替换完整挂载表 JSON（version:1,mounts:[]）；空数组卸载全部挂载。修改前先 wire_describe 读取当前表。scope 为 channel/global/session（session 仅支持向当前会话 sink.inject，sessionRef 填本会话 id），trigger 为 message/error；宿主下次读取挂载时生效。"
    };
    public IReadOnlyList<ToolParameter> Parameters => Name == "wire_describe"
        ? [new("class_id", "可选：图类 id；省略返回目录", 0, isRequired: false)]
        : [new("source", "完整 JSON 源码", 0)];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var source = resolvedInputs.FirstOrDefault() ?? "";
            var data = Name switch
            {
                "wire_describe" => _access.Describe(source),
                "wire_validate" => _access.Validate(source),
                "wire_apply" => _access.Apply(source),
                "wire_mount" => _access.Mount(source),
                _ => throw new InvalidOperationException("unknown wire operation")
            };
            return Task.FromResult(new ToolResult { Status = "success", Data = JsonSerializer.Serialize(data) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(new ToolResult { Status = "failed", Error = ex.Message });
        }
    }
}
