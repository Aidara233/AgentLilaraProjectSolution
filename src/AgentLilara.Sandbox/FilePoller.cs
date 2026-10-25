namespace AgentLilara.Sandbox;

/// <summary>一次轮询读到的文件消息。</summary>
public sealed record PolledMessage(string FileName, string Text, DateTime Time);

/// <summary>
/// 极简文件适配器：轮询目录，把新出现的文本文件当成消息。
/// 以"全路径 + 最后写入时间"去重，所以同名文件被重写会产生新消息。
/// </summary>
public sealed class FilePoller
{
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _patterns;

    public FilePoller(string directory, params string[] patterns)
    {
        DirectoryPath = directory;
        _patterns = patterns.Length == 0 ? new[] { "*.txt" } : patterns;
    }

    public string DirectoryPath { get; }

    public IReadOnlyList<PolledMessage> Poll()
    {
        if (!System.IO.Directory.Exists(DirectoryPath))
        {
            return Array.Empty<PolledMessage>();
        }

        var results = new List<PolledMessage>();

        foreach (var pattern in _patterns)
        {
            foreach (var path in System.IO.Directory
                         .EnumerateFiles(DirectoryPath, pattern)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                var info = new FileInfo(path);
                var key = $"{info.FullName}|{info.LastWriteTimeUtc.Ticks}";
                if (!_seen.Add(key))
                {
                    continue;
                }

                string text;
                try
                {
                    text = File.ReadAllText(path);
                }
                catch (IOException)
                {
                    // 正在写入：下一轮再读
                    _seen.Remove(key);
                    continue;
                }

                results.Add(new PolledMessage(info.Name, text.Trim(), info.LastWriteTimeUtc));
            }
        }

        results.Sort((a, b) => a.Time.CompareTo(b.Time));
        return results;
    }
}
