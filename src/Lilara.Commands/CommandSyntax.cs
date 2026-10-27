using System.Text;

namespace AgentCoreProcessor.Command;

internal sealed record ParsedCommand(string Name, string Args);

internal static class CommandSyntax
{
    public static ParsedCommand? Parse(string? content, string prefix)
    {
        content = content?.Trim();
        if (string.IsNullOrEmpty(content) || !content.StartsWith(prefix)) return null;
        var body = content[prefix.Length..].TrimStart();
        var spaceIdx = body.IndexOf(' ');
        var name = spaceIdx < 0 ? body : body[..spaceIdx];
        var args = spaceIdx < 0 ? "" : body[(spaceIdx + 1)..].Trim();
        return string.IsNullOrEmpty(name) ? null : new(name, args);
    }

    public static string FormatStepPrompt(CommandStep step)
    {
        var sb = new StringBuilder();
        sb.Append(step.Prompt);
        if (step.Options != null && step.Options.Count > 0)
        {
            sb.AppendLine();
            sb.Append("可选: ");
            sb.Append(string.Join(" / ", step.Options));
        }
        return sb.ToString();
    }
}
