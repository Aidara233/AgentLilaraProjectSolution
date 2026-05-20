namespace AgentCoreProcessor.Engine.Modules;

internal static class StringExtensions
{
    public static string Truncate(this string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen] + "...";
}
