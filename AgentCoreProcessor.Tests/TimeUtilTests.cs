using System;
using AgentCoreProcessor.Util;
using Xunit;

namespace AgentCoreProcessor.Tests
{
    /// <summary>
    /// 时区统一工具测试。验证存储全 UTC 约定下的三个边界转换。
    /// </summary>
    public class TimeUtilTests
    {
        [Fact]
        public void NormalizeToUtc_Local_ConvertsByLocalOffset()
        {
            var local = new DateTime(2026, 8, 23, 12, 30, 0, DateTimeKind.Local);
            var result = TimeUtil.NormalizeToUtc(local);
            Assert.Equal(DateTimeKind.Utc, result.Kind);
            Assert.Equal(local.ToUniversalTime(), result);
        }

        [Fact]
        public void NormalizeToUtc_Utc_StaysSame()
        {
            var utc = new DateTime(2026, 8, 23, 4, 30, 0, DateTimeKind.Utc);
            var result = TimeUtil.NormalizeToUtc(utc);
            Assert.Equal(DateTimeKind.Utc, result.Kind);
            Assert.Equal(utc, result);
        }

        [Fact]
        public void NormalizeToUtc_Unspecified_TreatedAsUtc()
        {
            var unspecified = new DateTime(2026, 8, 23, 4, 30, 0, DateTimeKind.Unspecified);
            var result = TimeUtil.NormalizeToUtc(unspecified);
            Assert.Equal(DateTimeKind.Utc, result.Kind);
            Assert.Equal(unspecified, result); // tick 原样，仅补 Kind
        }

        [Fact]
        public void ToLocalSafe_Unspecified_ConvertsToLocal()
        {
            // DB 读出为 Unspecified（sqlite-net ticks），tick 值本身是 UTC 墙上时间。
            // UTC 04:30 → 本地（UTC+8）应为 12:30。
            var unspecified = new DateTime(2026, 8, 23, 4, 30, 0, DateTimeKind.Unspecified);
            var result = TimeUtil.ToLocalSafe(unspecified);
            Assert.Equal(DateTimeKind.Local, result.Kind);
            var offset = TimeZoneInfo.Local.GetUtcOffset(unspecified);
            Assert.Equal(unspecified.Add(offset), result);
        }

        [Fact]
        public void TimeZoneAnchorText_ContainsUtcOffset()
        {
            var now = DateTime.Now;
            var text = TimeUtil.TimeZoneAnchorText(now);
            var offset = TimeZoneInfo.Local.GetUtcOffset(now);
            var sign = offset.TotalHours >= 0 ? "+" : "-";
            var expectedPrefix = $"UTC{sign}{offset.Hours:00}:{offset.Minutes:00}";
            Assert.StartsWith(expectedPrefix, text);
            Assert.Contains(TimeZoneInfo.Local.Id, text);
        }
    }
}