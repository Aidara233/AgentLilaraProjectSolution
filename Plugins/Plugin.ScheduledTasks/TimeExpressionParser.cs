using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Plugin.ScheduledTasks;

internal class ParseResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public DateTime? NextFireTime { get; set; }
    public bool IsRecurring { get; set; }

    public static ParseResult Ok(DateTime nextFire, bool recurring)
        => new() { Success = true, NextFireTime = nextFire, IsRecurring = recurring };

    public static ParseResult Fail(string error)
        => new() { Success = false, Error = error };
}

internal class ParsedExpression
{
    public int? Year;
    public int? Month;
    public int? Day;
    public int Hour;
    public int Minute;
    public int Second;
    public DayOfWeek? DayOfWeek;

    public bool IsRecurring => Year == null || Month == null || Day == null || DayOfWeek != null;
}

internal static class TimeExpressionParser
{
    private static readonly Regex RelativeRegex = new(
        @"^in\s+(\d+)\s*(s|sec|m|min|h|hr|d|day)s?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, DayOfWeek> DowMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mon"] = DayOfWeek.Monday, ["monday"] = DayOfWeek.Monday,
        ["tue"] = DayOfWeek.Tuesday, ["tuesday"] = DayOfWeek.Tuesday,
        ["wed"] = DayOfWeek.Wednesday, ["wednesday"] = DayOfWeek.Wednesday,
        ["thu"] = DayOfWeek.Thursday, ["thursday"] = DayOfWeek.Thursday,
        ["fri"] = DayOfWeek.Friday, ["friday"] = DayOfWeek.Friday,
        ["sat"] = DayOfWeek.Saturday, ["saturday"] = DayOfWeek.Saturday,
        ["sun"] = DayOfWeek.Sunday, ["sunday"] = DayOfWeek.Sunday,
    };

    private const string HelpText =
        "支持格式: 'in 30m' / 'in 2h' / 'in 1d' (相对时间), " +
        "'YYYY-MM-DD HH:MM' (指定时间), " +
        "'*-*-* 09:00' (每天9点), " +
        "'*-*-* 09:00 mon' (每周一9点), " +
        "'*-*-01 10:00' (每月1号10点), " +
        "'*-12-25 08:00' (每年12月25日8点). " +
        "年/月/日可用 * 通配.";

    public static ParseResult Parse(string expression, DateTime now)
    {
        expression = expression.Trim();

        // Format 1: relative time
        var m = RelativeRegex.Match(expression);
        if (m.Success)
            return ParseRelative(m, now);

        // Format 2: absolute time with optional wildcards
        return ParseAbsolute(expression, now);
    }

    public static DateTime? GetNextRecurrence(string expression, DateTime? lastFireTime)
    {
        if (lastFireTime == null) return null;
        return Parse(expression, lastFireTime.Value).NextFireTime;
    }

    private static ParseResult ParseRelative(Match m, DateTime now)
    {
        var num = int.Parse(m.Groups[1].Value);
        var unit = m.Groups[2].Value.ToLower();

        var next = unit switch
        {
            "s" or "sec" => now.AddSeconds(num),
            "m" or "min" => now.AddMinutes(num),
            "h" or "hr" => now.AddHours(num),
            "d" or "day" => now.AddDays(num),
            _ => now
        };

        return ParseResult.Ok(Normalize(next), false);
    }

    private static ParseResult ParseAbsolute(string expression, DateTime now)
    {
        // Split: "YYYY-MM-DD HH:MM[:SS] [dow]"
        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return ParseResult.Fail($"无法解析时间表达式。{HelpText}");

        var datePart = parts[0];
        var timePart = parts[1];
        var dowPart = parts.Length > 2 ? parts[2] : null;

        // Parse date
        var dateFields = datePart.Split('-');
        if (dateFields.Length != 3)
            return ParseResult.Fail($"日期格式错误，应为 YYYY-MM-DD。{HelpText}");

        if (!TryParseWildcardField(dateFields[0], out var year, 2000, 2100, "年份"))
            return ParseResult.Fail("年份超出范围(2000-2100)");
        if (!TryParseWildcardField(dateFields[1], out var month, 1, 12, "月份"))
            return ParseResult.Fail("月份超出范围(1-12)");
        if (!TryParseWildcardField(dateFields[2], out var day, 1, 31, "日"))
            return ParseResult.Fail("日超出范围(1-31)");

        // Parse time
        var timeFields = timePart.Split(':');
        if (timeFields.Length < 2)
            return ParseResult.Fail($"时间格式错误，应为 HH:MM 或 HH:MM:SS。{HelpText}");

        if (!int.TryParse(timeFields[0], out var hour) || hour < 0 || hour > 23)
            return ParseResult.Fail("小时超出范围(0-23)");
        if (!int.TryParse(timeFields[1], out var minute) || minute < 0 || minute > 59)
            return ParseResult.Fail("分钟超出范围(0-59)");
        var second = 0;
        if (timeFields.Length > 2)
        {
            if (!int.TryParse(timeFields[2], out second) || second < 0 || second > 59)
                return ParseResult.Fail("秒超出范围(0-59)");
        }

        // Parse DOW
        DayOfWeek? dow = null;
        if (dowPart != null && dowPart != "*")
        {
            if (!DowMap.TryGetValue(dowPart, out var d))
                return ParseResult.Fail($"无法识别的星期: {dowPart}。支持: mon/tue/wed/thu/fri/sat/sun");
            dow = d;
        }

        var expr = new ParsedExpression
        {
            Year = year,
            Month = month,
            Day = day,
            Hour = hour,
            Minute = minute,
            Second = second,
            DayOfWeek = dow
        };

        if (!expr.IsRecurring)
        {
            // All fields concrete: one-shot
            try
            {
                var dt = Normalize(new DateTime(year!.Value, month!.Value, day!.Value, hour, minute, second));
                return ParseResult.Ok(dt, false);
            }
            catch (ArgumentOutOfRangeException)
            {
                return ParseResult.Fail("指定的日期不存在（如2月30日）");
            }
        }

        // Recurring: compute next fire time >= now
        var nextFire = ComputeNextFire(expr, now);
        if (nextFire == null)
            return ParseResult.Fail("找不到未来的触发时间，任务已过期");

        return ParseResult.Ok(nextFire.Value, true);
    }

    private static bool TryParseWildcardField(string s, out int? value, int min, int max, string fieldName)
    {
        if (s == "*")
        {
            value = null;
            return true;
        }
        if (int.TryParse(s, out var num) && num >= min && num <= max)
        {
            value = num;
            return true;
        }
        value = null;
        return false;
    }

    private static DateTime? ComputeNextFire(ParsedExpression expr, DateTime after)
    {
        // Start from one minute after 'after' to avoid re-triggering the same minute
        var cursor = after.AddMinutes(1);
        // Reset seconds to 0 for clean comparison
        cursor = new DateTime(cursor.Year, cursor.Month, cursor.Day, cursor.Hour, cursor.Minute, 0);

        var maxIterations = 366 * 10; // safety limit: ~10 years of days
        for (int i = 0; i < maxIterations; i++)
        {
            var candidate = BuildCandidate(expr, cursor);
            if (candidate == null)
                return null;

            if (candidate.Value > after)
            {
                // Validate that concrete fields still match (they might have been advanced)
                if (MatchesConcrete(expr, candidate.Value))
                    return candidate.Value;
            }

            // Advance cursor to next possible candidate
            cursor = AdvanceCursor(expr, candidate ?? cursor);
        }

        return null;
    }

    private static DateTime Normalize(DateTime dt)
        => DateTime.SpecifyKind(dt, DateTimeKind.Local);

    private static DateTime? BuildCandidate(ParsedExpression expr, DateTime cursor)
    {
        int y = expr.Year ?? cursor.Year;
        int m = expr.Month ?? cursor.Month;
        int d = expr.Day ?? cursor.Day;

        var daysInMonth = DateTime.DaysInMonth(y, m);
        if (d > daysInMonth)
            return null;

        try
        {
            return Normalize(new DateTime(y, m, d, expr.Hour, expr.Minute, expr.Second));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTime AdvanceCursor(ParsedExpression expr, DateTime cursor)
    {
        if (expr.DayOfWeek != null)
            return cursor.Date.AddDays(1);

        if (expr.Day == null)
            return cursor.Date.AddDays(1);

        if (expr.Month == null)
        {
            // Monthly: advance to the specified day in the next month
            var nextMonth = cursor.AddMonths(1);
            return new DateTime(nextMonth.Year, nextMonth.Month, 1);
        }

        if (expr.Year == null)
        {
            // Yearly: advance to the specified month+day in the next year
            var targetMonth = expr.Month!.Value;
            var targetDay = expr.Day!.Value;
            // Jump to target month in the next year
            var nextYear = cursor.Year + 1;
            var maxDay = DateTime.DaysInMonth(nextYear, targetMonth);
            var day = Math.Min(targetDay, maxDay);
            return new DateTime(nextYear, targetMonth, day);
        }

        return cursor.Date.AddDays(1);
    }

    private static bool MatchesConcrete(ParsedExpression expr, DateTime dt)
    {
        if (expr.Year != null && dt.Year != expr.Year) return false;
        if (expr.Month != null && dt.Month != expr.Month) return false;
        if (expr.Day != null && dt.Day != expr.Day) return false;
        if (expr.DayOfWeek != null && dt.DayOfWeek != expr.DayOfWeek) return false;
        return true;
    }
}
