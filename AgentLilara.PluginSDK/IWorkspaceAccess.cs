using System.Threading;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK
{
    /// <summary>
    /// 工作目录访问接口。只允许 Workspace/ 内的文件。
    /// 插件禁止拿到目录字符串自行拼路径，路径解析一律走此接口。
    /// </summary>
    public interface IWorkspaceAccess
    {
        /// <summary>
        /// 解析到 Workspace 内绝对路径；越界返回 null。
        /// 相对路径拼 Workspace；绝对路径仅当落在 Workspace 内时返回。
        /// </summary>
        string? Resolve(string path);

        /// <summary>安全读取文件内容（编码自动探测）。文件不存在或越界返回 null。</summary>
        Task<string?> ReadAllTextAsync(string path, CancellationToken ct = default);

        /// <summary>安全写入文件（自动建目录）。越界抛出 InvalidOperationException。</summary>
        Task WriteAllTextAsync(string path, string content, CancellationToken ct = default);
    }
}