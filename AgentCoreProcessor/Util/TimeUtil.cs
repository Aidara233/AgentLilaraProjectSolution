using System;

namespace AgentCoreProcessor.Util
{
    /// <summary>
    /// 时区处理统一工具。存储全 UTC 约定下的三个边界转换：
    /// 写入归一化（NormalizeToUtc）、展示转本地（ToLocalSafe）、时区锚点文本（TimeZoneAnchorText）。
    /// </summary>
    internal static class TimeUtil
    {
        /// <summary>
        /// 入库归一化：统一转为 UTC 基准。
        /// - Kind=Local：ToUniversalTime()（按本地偏移转 UTC）
        /// - Kind=Utc：原样
        /// - Kind=Unspecified：SpecifyKind(Utc)（tick 原样，视为 UTC —— 含历史混存/外部解析的存量基准）
        /// </summary>
        public static DateTime NormalizeToUtc(DateTime t)
            => t.Kind switch
            {
                DateTimeKind.Utc => t,
                DateTimeKind.Local => t.ToUniversalTime(),
                _ => DateTime.SpecifyKind(t, DateTimeKind.Utc)
            };

        /// <summary>
        /// 展示端统一：先按 UTC 解释（DB 读出为 Unspecified，ToLocalTime 对 Unspecified 不做换算），再转本地。
        /// </summary>
        public static DateTime ToLocalSafe(DateTime t) => NormalizeToUtc(t).ToLocalTime();

        /// <summary>
        /// 时区锚点文本，供注入 agent 上下文时标明时区。例："UTC+08:00 (China Standard Time)"。
        /// 动态跟随系统时区（含夏令时偏移），不硬编码。
        /// </summary>
        public static string TimeZoneAnchorText(DateTime now)
        {
            var offset = TimeZoneInfo.Local.GetUtcOffset(now);
            var sign = offset.TotalHours >= 0 ? "+" : "-";
            var utcLabel = $"UTC{sign}{offset.Hours:00}:{offset.Minutes:00}";
            var tzName = TimeZoneInfo.Local.Id;
            // Id 形如 "China Standard Time"（Windows）/ "Asia/Shanghai"（IANA），直接展示即可
            return $"{utcLabel} ({tzName})";
        }
    }
}