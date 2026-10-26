using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Tool;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Core
{
    /// <summary>
    /// 登记一个"等着某件事发生"的订阅。
    /// <para>这是把"我待会儿发邮件给你，你盯着点"变成机械规则的入口：
    /// 模型说一次（花一次 token），之后匹配、等待、唤醒全归框架，命中前零成本。</para>
    /// </summary>
    internal class WatchTool : ITool
    {
        private readonly IWatchAccess? _access;

        public WatchTool(IToolContext context) => _access = context.GetService<IWatchAccess>();

        public string Name => "watch";

        /// <summary>
        /// 描述里实时拼上"当前真实存在哪些信源、各支持哪些字段"。
        /// 静态文案会让模型只能猜 source 该填什么，猜错的表现是"订阅成功但永远不命中"。
        /// </summary>
        public string Description
        {
            get
            {
                const string baseText =
                    "登记一个订阅：等某个信源上出现符合条件的消息，命中后框架会把内容送到当前会话，你不需要反复查询。" +
                    "用于「我待会儿发邮件给你」「有人回复我时告诉我」这类承诺。一次性：命中或过期后自动失效。";

                var sources = _access?.DescribeSources();
                if (sources is null || sources.Count == 0)
                {
                    return baseText;
                }

                var parts = sources.Select(source => source.Fields.Count == 0
                    ? source.Kind
                    : $"{source.Kind}（字段：{string.Join("/", source.Fields)}）");

                return baseText + " 可用信源：" + string.Join("；", parts) + "。";
            }
        }

        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("source", "信源类别，例如 mail", 0),
            new("from", "（可选）只认这个发件人，留空表示不限", 1, isRequired: false),
            new("note", "（可选）为什么在等；会随命中内容一起交回给你，供事后理解上下文", 2, isRequired: false),
            new("ttl_minutes", "（可选）有效分钟数；0 或省略表示不过期", 3, isRequired: false),
            new("max_fires", "（可选）最多触发几次，默认 1", 4, isRequired: false),
        ];

        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (_access is null)
            {
                return Fail("订阅功能未启用");
            }

            var channelId = ToolExecutionScope.Current?.ChannelId ?? 0;
            if (channelId <= 0)
            {
                return Fail("当前会话没有绑定频道，无法建立订阅");
            }

            var request = new WatchRequest
            {
                Source = Arg(resolvedInputs, 0),
                From = OptionalArg(resolvedInputs, 1),
                Note = OptionalArg(resolvedInputs, 2),
                TtlMinutes = ParseInt(OptionalArg(resolvedInputs, 3)),
                MaxFires = ParseInt(OptionalArg(resolvedInputs, 4), fallback: 1),
            };

            var result = _access.Create(channelId, request);
            if (!result.Success)
            {
                return Fail(result.Error ?? "创建失败");
            }

            var summary = new StringBuilder();
            summary.Append("已登记订阅 ").Append(result.WatchId).Append("：等待 ").Append(request.Source);
            if (!string.IsNullOrWhiteSpace(request.From))
            {
                summary.Append("（来自 ").Append(request.From).Append("）");
            }

            summary.Append(request.TtlMinutes > 0
                ? $"；{request.TtlMinutes} 分钟内有效"
                : "；不过期");

            summary.Append($"；最多触发 {request.MaxFires} 次。命中后内容会自动送到本会话，不必主动查询。");

            return Task.FromResult(new ToolResult { Status = "success", Data = summary.ToString() });
        }

        private static string Arg(List<string> inputs, int index) =>
            index < inputs.Count ? inputs[index]?.Trim() ?? string.Empty : string.Empty;

        private static string? OptionalArg(List<string> inputs, int index)
        {
            var value = Arg(inputs, index);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static int ParseInt(string? text, int fallback = 0) =>
            int.TryParse(text, out var value) ? Math.Max(0, value) : fallback;

        private static Task<ToolResult> Fail(string error) =>
            Task.FromResult(new ToolResult { Status = "failed", Error = error });
    }

    /// <summary>取消一条订阅。</summary>
    internal class UnwatchTool : ITool
    {
        private readonly IWatchAccess? _access;

        public UnwatchTool(IToolContext context) => _access = context.GetService<IWatchAccess>();

        public string Name => "unwatch";

        public string Description => "取消一条先前登记的订阅（不知道 id 时先用 list_watches 查）。";

        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("watch_id", "订阅 id（创建时返回的那个）", 0),
        ];

        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (_access is null)
            {
                return Task.FromResult(new ToolResult { Status = "failed", Error = "订阅功能未启用" });
            }

            var channelId = ToolExecutionScope.Current?.ChannelId ?? 0;
            var watchId = resolvedInputs.Count > 0 ? resolvedInputs[0]?.Trim() : null;

            if (string.IsNullOrWhiteSpace(watchId))
            {
                return Task.FromResult(new ToolResult { Status = "failed", Error = "watch_id 必填" });
            }

            return Task.FromResult(_access.Cancel(channelId, watchId)
                ? new ToolResult { Status = "success", Data = $"已取消订阅 {watchId}" }
                : new ToolResult { Status = "failed", Error = "没有找到这条订阅（或它不属于当前频道）" });
        }
    }

    /// <summary>列出当前频道仍然有效的订阅。</summary>
    internal class ListWatchesTool : ITool
    {
        private readonly IWatchAccess? _access;

        public ListWatchesTool(IToolContext context) => _access = context.GetService<IWatchAccess>();

        public string Name => "list_watches";

        public string Description => "列出当前频道还在等的订阅（还在盯着什么）。";

        public IReadOnlyList<ToolParameter> Parameters => [];

        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (_access is null)
            {
                return Task.FromResult(new ToolResult { Status = "failed", Error = "订阅功能未启用" });
            }

            var channelId = ToolExecutionScope.Current?.ChannelId ?? 0;
            var watches = _access.List(channelId);

            if (watches.Count == 0)
            {
                return Task.FromResult(new ToolResult { Status = "success", Data = "当前没有登记中的订阅。" });
            }

            var text = string.Join("；", watches.Select(w =>
                $"{w.Id} ← {w.Source}" +
                (string.IsNullOrWhiteSpace(w.Note) ? string.Empty : $"（{w.Note}）") +
                $" 已触发 {w.FiredCount}/{w.MaxFires}"));

            return Task.FromResult(new ToolResult { Status = "success", Data = "登记中的订阅：" + text });
        }
    }
}
