using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 定时任务工具：创建/取消定时任务。频道循环和系统循环都可使用。
    /// </summary>
    internal class ScheduledTaskTool : ITool
    {
        public string Name => "create_scheduled_task";
        public string Description => "创建定时任务。支持: 相对时间(30m/2h/1d)、绝对时间(09:00/2026-05-10 14:00)、重复(every 1h/daily 09:00/weekly mon 09:00)";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("时间表达式", "触发时间。示例: 30m, 2h, 09:00, 2026-05-10 14:00, every 1h, daily 09:00, weekly mon 14:00", 0),
            new("描述", "任务描述（触发时会看到）", 1),
            new("载荷", "可选：触发时附带的详细内容", 2)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public bool ContinueLoop => true;
        public string? CapabilitySummary => "创建定时/延时任务";

        private readonly ISystemContext ctx;
        private Func<string>? ownerIdProvider;

        public ScheduledTaskTool(ISystemContext ctx)
        {
            this.ctx = ctx;
        }

        public void SetOwnerProvider(Func<string> provider) => ownerIdProvider = provider;

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 2 || string.IsNullOrWhiteSpace(resolvedInputs[0]) || string.IsNullOrWhiteSpace(resolvedInputs[1]))
                return new ToolResult { Status = "failed", Error = "时间表达式和描述不能为空" };

            var timeExpr = resolvedInputs[0].Trim();
            var description = resolvedInputs[1];
            var payload = resolvedInputs.Count > 2 ? resolvedInputs[2] : null;

            var parsed = ScheduleParser.Parse(timeExpr);
            if (parsed == null)
                return new ToolResult { Status = "failed", Error = $"无法解析时间表达式: {timeExpr}" };

            var ownerId = ownerIdProvider?.Invoke() ?? "system";
            var ownerType = ownerId == "system" ? "system" : "channel";

            var task = new ScheduledTask
            {
                OwnerType = ownerType,
                OwnerId = ownerId,
                Description = description,
                NextFireTime = parsed.Value.NextFire,
                RepeatIntervalSeconds = parsed.Value.RepeatSeconds,
                CronRule = parsed.Value.CronRule,
                Payload = payload,
                IsActive = true,
                CreatedAt = DateTime.Now
            };

            await ctx.ScheduledTasks.CreateAsync(task);

            var repeatInfo = parsed.Value.RepeatSeconds > 0
                ? $"，重复间隔 {FormatDuration(parsed.Value.RepeatSeconds)}"
                : parsed.Value.CronRule != null ? $"，规则: {parsed.Value.CronRule}" : "（一次性）";

            return new ToolResult
            {
                Status = "success",
                Data = $"定时任务已创建: #{task.Id}\n下次触发: {task.NextFireTime:yyyy-MM-dd HH:mm:ss}{repeatInfo}"
            };
        }

        private static string FormatDuration(int seconds)
        {
            if (seconds >= 86400) return $"{seconds / 86400}天";
            if (seconds >= 3600) return $"{seconds / 3600}小时";
            if (seconds >= 60) return $"{seconds / 60}分钟";
            return $"{seconds}秒";
        }
    }

    /// <summary>取消定时任务工具。</summary>
    internal class CancelScheduledTaskTool : ITool
    {
        public string Name => "cancel_scheduled_task";
        public string Description => "取消指定的定时任务";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("任务ID", "定时任务的 ID", 0)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public bool ContinueLoop => true;

        private readonly ISystemContext ctx;

        public CancelScheduledTaskTool(ISystemContext ctx)
        {
            this.ctx = ctx;
        }

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || !int.TryParse(resolvedInputs[0], out var taskId))
                return new ToolResult { Status = "failed", Error = "请提供有效的任务 ID" };

            var task = await ctx.ScheduledTasks.GetByIdAsync(taskId);
            if (task == null)
                return new ToolResult { Status = "failed", Error = $"任务 #{taskId} 不存在" };

            await ctx.ScheduledTasks.CancelAsync(taskId);
            return new ToolResult { Status = "success", Data = $"定时任务 #{taskId} 已取消: {task.Description}" };
        }
    }

    /// <summary>时间表达式解析器。</summary>
    internal static class ScheduleParser
    {
        public struct ParseResult
        {
            public DateTime NextFire;
            public int RepeatSeconds;
            public string? CronRule;
        }

        public static ParseResult? Parse(string expr)
        {
            expr = expr.Trim().ToLowerInvariant();

            // 相对时间: 30m, 2h, 1d, 90s
            var relMatch = Regex.Match(expr, @"^(\d+)\s*(s|m|min|h|hr|d|day)s?$");
            if (relMatch.Success)
            {
                var amount = int.Parse(relMatch.Groups[1].Value);
                var unit = relMatch.Groups[2].Value;
                var seconds = unit switch
                {
                    "s" => amount,
                    "m" or "min" => amount * 60,
                    "h" or "hr" => amount * 3600,
                    "d" or "day" => amount * 86400,
                    _ => 0
                };
                if (seconds > 0)
                    return new ParseResult { NextFire = DateTime.Now.AddSeconds(seconds), RepeatSeconds = 0 };
            }

            // 重复: every 30m, every 2h, every 1d
            var everyMatch = Regex.Match(expr, @"^every\s+(\d+)\s*(s|m|min|h|hr|d|day)s?$");
            if (everyMatch.Success)
            {
                var amount = int.Parse(everyMatch.Groups[1].Value);
                var unit = everyMatch.Groups[2].Value;
                var seconds = unit switch
                {
                    "s" => amount,
                    "m" or "min" => amount * 60,
                    "h" or "hr" => amount * 3600,
                    "d" or "day" => amount * 86400,
                    _ => 0
                };
                if (seconds > 0)
                    return new ParseResult
                    {
                        NextFire = DateTime.Now.AddSeconds(seconds),
                        RepeatSeconds = seconds,
                        CronRule = $"every {amount}{unit}"
                    };
            }

            // daily HH:mm
            var dailyMatch = Regex.Match(expr, @"^daily\s+(\d{1,2}):(\d{2})$");
            if (dailyMatch.Success)
            {
                var hour = int.Parse(dailyMatch.Groups[1].Value);
                var minute = int.Parse(dailyMatch.Groups[2].Value);
                var next = GetNextDailyTime(hour, minute);
                return new ParseResult { NextFire = next, RepeatSeconds = 86400, CronRule = $"daily {hour:D2}:{minute:D2}" };
            }

            // weekly <day> HH:mm
            var weeklyMatch = Regex.Match(expr, @"^weekly\s+(mon|tue|wed|thu|fri|sat|sun)\s+(\d{1,2}):(\d{2})$");
            if (weeklyMatch.Success)
            {
                var dayStr = weeklyMatch.Groups[1].Value;
                var hour = int.Parse(weeklyMatch.Groups[2].Value);
                var minute = int.Parse(weeklyMatch.Groups[3].Value);
                var dow = ParseDayOfWeek(dayStr);
                var next = GetNextWeeklyTime(dow, hour, minute);
                return new ParseResult { NextFire = next, RepeatSeconds = 604800, CronRule = $"weekly {dayStr} {hour:D2}:{minute:D2}" };
            }

            // 绝对时间 HH:mm（今天或明天）
            var timeMatch = Regex.Match(expr, @"^(\d{1,2}):(\d{2})$");
            if (timeMatch.Success)
            {
                var hour = int.Parse(timeMatch.Groups[1].Value);
                var minute = int.Parse(timeMatch.Groups[2].Value);
                var next = GetNextDailyTime(hour, minute);
                return new ParseResult { NextFire = next, RepeatSeconds = 0 };
            }

            // 绝对时间 yyyy-MM-dd HH:mm
            if (DateTime.TryParseExact(expr, new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "MM-dd HH:mm" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var absTime))
            {
                if (absTime < DateTime.Now)
                    return null;
                return new ParseResult { NextFire = absTime, RepeatSeconds = 0 };
            }

            return null;
        }

        /// <summary>计算下次触发时间（用于重复任务）。</summary>
        public static DateTime? ComputeNextFire(ScheduledTask task)
        {
            if (task.RepeatIntervalSeconds > 0)
                return DateTime.Now.AddSeconds(task.RepeatIntervalSeconds);

            if (!string.IsNullOrEmpty(task.CronRule))
            {
                var parsed = Parse(task.CronRule);
                return parsed?.NextFire;
            }

            return null;
        }

        private static DateTime GetNextDailyTime(int hour, int minute)
        {
            var today = DateTime.Today.AddHours(hour).AddMinutes(minute);
            return today > DateTime.Now ? today : today.AddDays(1);
        }

        private static DateTime GetNextWeeklyTime(DayOfWeek dow, int hour, int minute)
        {
            var now = DateTime.Now;
            var daysUntil = ((int)dow - (int)now.DayOfWeek + 7) % 7;
            var next = DateTime.Today.AddDays(daysUntil).AddHours(hour).AddMinutes(minute);
            if (next <= now) next = next.AddDays(7);
            return next;
        }

        private static DayOfWeek ParseDayOfWeek(string s) => s switch
        {
            "mon" => DayOfWeek.Monday,
            "tue" => DayOfWeek.Tuesday,
            "wed" => DayOfWeek.Wednesday,
            "thu" => DayOfWeek.Thursday,
            "fri" => DayOfWeek.Friday,
            "sat" => DayOfWeek.Saturday,
            "sun" => DayOfWeek.Sunday,
            _ => DayOfWeek.Monday
        };
    }
}
