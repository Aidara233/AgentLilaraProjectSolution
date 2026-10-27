using System.Text;
using AgentLilara.PluginSDK;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Tool;

/// <summary>渲染已完成可见性筛选的工具序列，保持调用方给定顺序。</summary>
internal static class ToolDescriptionRenderer
{
    public static string Render(IEnumerable<ITool> tools)
    {
        var sb = new StringBuilder();
        int i = 1;
        foreach (var tool in tools)
        {
            sb.AppendLine($"工具{i}：{tool.Name}");
            sb.AppendLine($"描述：{tool.Description}");
            if (tool.Parameters.Count > 0)
            {
                var paramParts = tool.Parameters.Select(p => $"inputs[{p.Index}] = {p.Name}");
                sb.AppendLine($"参数：{string.Join(", ", paramParts)}");
            }
            var example = new
            {
                tool = tool.Name,
                inputs = tool.Parameters.Select(p => $"({p.Name})").ToArray()
            };
            sb.AppendLine($"示例：{JsonConvert.SerializeObject(example, Formatting.None)}<over>");
            sb.AppendLine();
            i++;
        }
        return sb.ToString().TrimEnd();
    }
}
