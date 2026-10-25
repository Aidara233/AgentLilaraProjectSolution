using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Lilara.Core.Kernel;
using AlpITool = AgentLilara.PluginSDK.ITool;
using AlpToolResult = AgentLilara.PluginSDK.ToolResult;

namespace AgentLilara.KernelBridge;

/// <summary>包装后的工具表 + 转换过程中发现的问题。</summary>
public sealed record ToolCatalog(
    IReadOnlyList<ITool> Tools,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Notes);

/// <summary>
/// 原始 <see cref="AlpToolResult"/> 的交接账。
///
/// <para>⚠️ 它存在的理由：ALC 的 <see cref="ToolOutcome"/> 只承载**内容**，
/// 而 ALP 的宿主副作用（speak 的文本、`RequestWait`、end_session 的 reason）
/// 要读**原始 <see cref="AlpToolResult"/>**。转换时把它原样记下来，
/// 宿主在一步结束后按 <c>CallId</c> 取走——副作用仍然只由宿主判定。</para>
/// </summary>
public sealed class ToolResultLedger
{
    private readonly ConcurrentDictionary<string, AlpToolResult> _results =
        new(StringComparer.Ordinal);

    public void Record(string callId, AlpToolResult result) => _results[callId] = result;

    /// <summary>取走并移除（⚠️ 一次性：同一次调用的结果只该被消费一次）。</summary>
    public AlpToolResult? Take(string callId) =>
        _results.TryRemove(callId, out var result) ? result : null;

    public IReadOnlyDictionary<string, AlpToolResult> Snapshot() =>
        new Dictionary<string, AlpToolResult>(_results, StringComparer.Ordinal);
}

/// <summary>
/// ALP <see cref="AlpITool"/> → ALC <see cref="ITool"/>。
///
/// <para>⚠️ **这是执行面的桥，不是声明面的桥**：工具表进请求仍然走 `SessionBrainTool` DTO；
/// 这里负责「模型发来的原始 JSON 参数 → ALP 的位置化 <c>List&lt;string&gt;</c>」与
/// 「ALP <c>ToolResult</c> → ALC <c>ToolOutcome</c>」。</para>
///
/// <para>⚠️ 宿主副作用（speak / end_session / wait / escalate …）**不在这里执行**：
/// 那个循环仍由 ALP 的 `ApplyToolEffectAsync` 判定。桥只跑工具本体。</para>
/// </summary>
public static class ToolBridge
{
    /// <summary>ALP 工具没有权限类别——按 ADR 的约定，声明不了自己的那一侧默认 <see cref="PermissionClass.Writing"/>。</summary>
    public const PermissionClass DefaultPermission = PermissionClass.Writing;

    public static ToolCatalog Wrap(
        IEnumerable<AlpITool> tools,
        Func<AlpITool, bool>? visible = null,
        Func<AlpITool, PermissionClass>? permission = null,
        ToolResultLedger? ledger = null)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var result = new List<ITool>();
        var conflicts = new List<string>();
        var notes = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var usedDefaultPermission = false;

        foreach (var tool in tools)
        {
            if (tool is null) continue;
            if (visible is not null && !visible(tool)) continue;

            if (!seen.Add(tool.Name))
            {
                // ⚠️ 重名必须**响亮地报**：静默保留一个会让「模型调的是哪一个」变成偶然。
                conflicts.Add($"工具重名：'{tool.Name}'（保留先出现的那一个，后面的被丢弃）");
                continue;
            }

            if (permission is null) usedDefaultPermission = true;
            result.Add(new AlpToolAdapter(
                tool, permission?.Invoke(tool) ?? DefaultPermission, ledger));
        }

        if (usedDefaultPermission)
            notes.Add($"ALP 工具没有权限类别，未显式映射的全部按 {DefaultPermission} 处理");

        return new ToolCatalog(result, conflicts, notes);
    }
}

/// <summary>单条 ALP 工具的适配器。</summary>
internal sealed class AlpToolAdapter : ITool
{
    private readonly AlpITool _tool;
    private readonly ToolResultLedger? _ledger;

    public AlpToolAdapter(AlpITool tool, PermissionClass permission, ToolResultLedger? ledger = null)
    {
        _tool = tool;
        _ledger = ledger;
        PermissionClass = permission;
        // ⚠️ schema 逐字发出：它是请求字节，规范化一下就可能不再合法（同 ALC 的既有纪律）。
        ParametersSchemaJson = tool.GetInputSchema().ToJsonString();
    }

    public string Name => _tool.Name;
    public string Description => _tool.Description;
    public PermissionClass PermissionClass { get; }
    public string ParametersSchemaJson { get; }

    public async Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_tool.Timeout > TimeSpan.Zero)
            timeout.CancelAfter(_tool.Timeout);

        try
        {
            var inputs = MapInputs(call.ArgumentsJson);
            var result = await _tool.ExecuteAsync(inputs, timeout.Token).ConfigureAwait(false);
            _ledger?.Record(call.CallId, result);
            return ToOutcome(result, context);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 超时是**结果**，不是异常（协议要求 tool_use 必须有配对的 tool_result）。
            return Failed(call.CallId, $"执行超时（{_tool.Timeout.TotalSeconds}s）");
        }
        catch (OperationCanceledException)
        {
            // 真正的取消要冒上去，否则会话停不下来。
            throw;
        }
        catch (Exception ex)
        {
            return Failed(call.CallId, $"工具执行异常：{ex.Message}");
        }
    }

    private ToolOutcome Failed(string callId, string error)
    {
        var result = new AlpToolResult { Status = "failed", Error = error };
        _ledger?.Record(callId, result);
        return Text($"[failed] {error}");
    }

    /// <summary>
    /// 命名 JSON → 位置化 inputs。
    /// ⚠️ 顺序按 <see cref="AgentLilara.PluginSDK.ToolParameter.Index"/>，与 Legacy 的
    /// 「按 schema properties 顺序」等价；不做类型转换，字符串化规则与
    /// `NativeToolCallHandler.RawString` 逐条对齐。
    /// </summary>
    private List<string> MapInputs(string argumentsJson)
    {
        var parameters = _tool.Parameters.OrderBy(p => p.Index).ToList();
        var inputs = parameters.Select(_ => "").ToList();

        JsonObject? args;
        try
        {
            args = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson)
                as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            // 解析失败时 Legacy 会把原始 JSON 塞进第一个位置；保持同一行为。
            if (inputs.Count == 0) return [argumentsJson];
            inputs[0] = argumentsJson;
            return inputs;
        }

        if (args is null) return inputs;

        if (parameters.Count == 0)
        {
            // 没有声明参数：按 JSON 属性顺序填（同 Legacy 的 fallback）。
            return args.Select(pair => RawString(pair.Value)).ToList();
        }

        for (var i = 0; i < parameters.Count; i++)
        {
            if (args.TryGetPropertyValue(parameters[i].Name, out var node))
                inputs[i] = RawString(node);
        }

        return inputs;
    }

    private static string RawString(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            try { return value.GetValue<string>() ?? ""; }
            catch (InvalidOperationException) { }
            catch (FormatException) { }
        }
        return node?.ToString() ?? "";
    }

    private static ToolOutcome ToOutcome(AlpToolResult result, IToolContext context)
    {
        var content = new List<Block>();

        if (result.IsSuccess)
        {
            if (!string.IsNullOrEmpty(result.Data))
                content.Add(new Block.TextBlock(result.Data));
        }
        else
        {
            var detail = result.Error ?? result.Data ?? "工具未返回说明";
            content.Add(new Block.TextBlock($"[{result.Status}] {detail}"));
        }

        foreach (var attachment in result.Attachments ?? [])
        {
            if (ToAttachmentBlock(attachment, context) is { } block)
                content.Add(block);
            else
                // ⚠️ 不能静默丢：模型需要知道「这里本来有一张图」。
                content.Add(new Block.TextBlock(
                    $"[附件无法传递：{attachment.Type}（既没有 base64，也读不到文件）]"));
        }

        return new ToolOutcome { Content = content };
    }

    private static Block? ToAttachmentBlock(
        AgentLilara.PluginSDK.ContentAttachment attachment, IToolContext context)
    {
        var bytes = Decode(attachment.Base64Data);
        if (bytes is null && !string.IsNullOrEmpty(attachment.FilePath) && File.Exists(attachment.FilePath))
        {
            try { bytes = File.ReadAllBytes(attachment.FilePath); }
            catch (IOException) { bytes = null; }
            catch (UnauthorizedAccessException) { bytes = null; }
        }

        if (bytes is null) return null;

        var sha = context.EnsureStored(bytes);
        var mediaType = attachment.MediaType ?? "image/png";
        return attachment.Type == "image"
            ? new Block.ImageBlock(sha, mediaType)
            : new Block.StoreBlock(sha, mediaType);
    }

    private static byte[]? Decode(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try { return Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }
    }

    private static ToolOutcome Text(string text) =>
        new() { Content = [new Block.TextBlock(text)] };
}
