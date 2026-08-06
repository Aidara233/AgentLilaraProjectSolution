using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.BasicTools
{
    /// <summary>
    /// 按条件查询聊天记录。纯查询工具，不产生输出。
    /// 结果按时间升序（旧→新）返回。
    /// </summary>
    [ToolMeta(Group = null, ContinueLoop = false)]
    public class SearchChatHistoryTool : ITool
    {
        private readonly IChannelAccess? _channelAccess;

        public SearchChatHistoryTool() { }

        public SearchChatHistoryTool(IChannelAccess channelAccess) => _channelAccess = channelAccess;

        public string Name => "search_chat_history";

        public string Description =>
            "按条件查询聊天记录，返回时间升序（旧→新）的 JSON 数组，每条含 id、sender、content、time、isBot。"
            + "至少提供 channel / keyword / time_from / time_to 之一。"
            + "after_id（增量：取该 id 之后的消息）与 before_id（翻旧：取该 id 之前最近 N 条）为游标模式，必须指定单个 channel。"
            + "channel 省略则在全部频道中搜索（仅 keyword / 时间模式可用）。"
            + "time_from / time_to 用 ISO 时间，如 2026-08-01 或 2026-08-01T12:00:00。";

        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("channel", "频道名（来自 list_channels 的 name）。省略 = 全部频道；游标模式必须指定单个频道。", 0, false),
            new("keyword", "内容关键词，匹配消息文本或发送者名。", 1, false),
            new("time_from", "起始时间（ISO，含）。如 2026-08-01 或 2026-08-01T12:00:00。", 2, false),
            new("time_to", "结束时间（ISO，含）。", 3, false),
            new("after_id", "增量拉取：返回 id 大于该值的消息（需指定单个 channel）。", 4, false),
            new("before_id", "翻旧：返回 id 小于等于该值的最近 N 条（含该条，需指定单个 channel）。", 5, false),
            new("limit", "返回条数，1-100，默认 20。", 6, false)
        ];

        public TimeSpan Timeout => TimeSpan.FromSeconds(15);

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (_channelAccess == null)
                return new ToolResult { Status = "failed", Error = "频道服务不可用" };

            var channel = Get(resolvedInputs, 0);
            var keyword = Get(resolvedInputs, 1);
            var timeFrom = TryParseTime(Get(resolvedInputs, 2));
            var timeTo = TryParseTime(Get(resolvedInputs, 3));
            var afterId = TryParseInt(Get(resolvedInputs, 4));
            var beforeId = TryParseInt(Get(resolvedInputs, 5));
            var limit = TryParseInt(Get(resolvedInputs, 6)) ?? 20;

            if (timeFrom is not null && timeTo is not null && timeFrom > timeTo)
                return new ToolResult { Status = "failed", Error = "time_from 晚于 time_to" };

            if (afterId.HasValue && beforeId.HasValue)
                return new ToolResult { Status = "failed", Error = "after_id 与 before_id 不能同时使用" };

            var query = new ChannelQuery
            {
                ChannelNames = string.IsNullOrWhiteSpace(channel) ? null : new List<string> { channel },
                Keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword,
                TimeFrom = timeFrom,
                TimeTo = timeTo,
                AfterId = afterId,
                BeforeId = beforeId,
                Limit = limit
            };

            if (query.ChannelNames is null && query.Keyword is null
                && query.TimeFrom is null && query.TimeTo is null
                && query.AfterId is null && query.BeforeId is null)
                return new ToolResult { Status = "failed", Error = "至少提供一个查询条件（channel / keyword / time_from / time_to / after_id / before_id）" };

            var messages = await _channelAccess.SearchChatAsync(query);
            var payload = messages.Select(m => new
            {
                id = m.Id,
                sender = m.UserName,
                content = m.Content,
                time = m.Timestamp.ToString("O"),
                isBot = m.IsBot
            });
            return new ToolResult { Status = "success", Data = JsonSerializer.Serialize(payload) };
        }

        private static string Get(List<string> inputs, int index)
            => index < inputs.Count ? inputs[index] ?? "" : "";

        private static DateTime? TryParseTime(string s)
            => DateTime.TryParse(s, out var t) ? t : null;

        private static int? TryParseInt(string s)
            => int.TryParse(s, out var n) ? n : null;
    }
}
