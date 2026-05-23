using System;

namespace AgentCoreProcessor.Engine;

/// <summary>
/// 统一循环标识符。所有引擎循环使用相同格式的 ID。
/// </summary>
public static class LoopId
{
    public const string System = "system";

    public static string ForChannel(int channelId) => $"channel:{channelId}";
    public static string ForTask(string sessionId) => $"task:{sessionId}";
    public static string ForReview(string reviewType) => $"review:{reviewType}";

    public static bool IsChannel(string loopId, out int channelId)
    {
        if (loopId != null && loopId.StartsWith("channel:"))
        {
            if (int.TryParse(loopId.AsSpan(8), out channelId))
                return true;
        }
        channelId = -1;
        return false;
    }

    public static bool IsSystem(string loopId) => loopId == System;
    public static bool IsTask(string loopId) => loopId != null && loopId.StartsWith("task:");
    public static bool IsReview(string loopId) => loopId != null && loopId.StartsWith("review:");

    public static string ExtractLoopType(string loopId)
    {
        if (loopId == System) return "system";
        if (loopId == null) return "unknown";
        int colonIdx = loopId.IndexOf(':');
        return colonIdx > 0 ? loopId[..colonIdx] : loopId;
    }
}
