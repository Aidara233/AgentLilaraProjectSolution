// Plugins/Plugin.DocumentTools/DocumentRangeParser.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace Plugin.DocumentTools;

public static class DocumentRangeParser
{
    /// <summary>
    /// 解析范围表达式，返回 1-based 索引列表。
    /// 支持: "1-5" / "1,3,7" / "3-" / "1-5,8,10-12"
    /// 自动 clamp 到 [1, max] 范围，去重排序。
    /// </summary>
    public static List<int> Parse(string? input, int max)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new List<int>();

        var result = new HashSet<int>();
        var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            var dashIdx = trimmed.IndexOf('-');

            if (dashIdx < 0)
            {
                // 单个数字
                if (int.TryParse(trimmed, out var n) && n >= 1)
                    result.Add(Math.Min(n, max));
            }
            else if (dashIdx == 0)
            {
                // "-5" → 1-5
                if (int.TryParse(trimmed[1..], out var end) && end >= 1)
                    AddRange(result, 1, Math.Min(end, max));
            }
            else if (dashIdx == trimmed.Length - 1)
            {
                // "3-" → 3 到末尾
                if (int.TryParse(trimmed[..^1], out var start) && start >= 1)
                    AddRange(result, Math.Min(start, max), max);
            }
            else
            {
                // "1-5"
                var sides = trimmed.Split('-', 2);
                if (int.TryParse(sides[0], out var s) && int.TryParse(sides[1], out var e))
                {
                    s = Math.Max(1, s);
                    e = Math.Min(max, e);
                    if (s <= e)
                        AddRange(result, s, e);
                }
            }
        }

        return result.OrderBy(x => x).ToList();
    }

    private static void AddRange(HashSet<int> set, int start, int end)
    {
        for (var i = start; i <= end; i++)
            set.Add(i);
    }

    /// <summary>
    /// 截断文本，保留结构标记。超出时报告总量。
    /// </summary>
    public static string Truncate(string text, int maxLen, string itemLabel, int totalCount)
    {
        if (text.Length <= maxLen) return text;
        return text[..maxLen] + $"\n... (结果已截断，共 {totalCount} {itemLabel})";
    }
}
