using System.Text.Json;

namespace Plugin.Email;

/// <summary>
/// 游标持久化。
/// <para>抽出来是为了可测：游标读写错了，表现是"重放整个收件箱"或"永久漏信"，
/// 这两种都不是看一眼日志能发现的。</para>
/// <para>两条不变量：读失败一律当作"没有游标"（宁可丢一轮、重取基线，也不拿坏数据去比 UID）；
/// 写用临时文件替换，避免半截文件被下一次读成损坏。</para>
/// </summary>
public sealed class MailCursorStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public MailCursorStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("游标路径不能为空", nameof(path));
        }

        Path = path;
    }

    /// <summary>游标文件路径。</summary>
    public string Path { get; }

    /// <summary>读游标。文件不存在或内容损坏都返回 null。</summary>
    public MailCursor? Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<MailCursor>(File.ReadAllText(Path), JsonOptions);
        }
        catch (Exception)
        {
            // 损坏的游标不能拿去比 UID：那会得出"这些全是新邮件"或"这些全是旧的"这类荒谬结论
            return null;
        }
    }

    /// <summary>写游标。返回 false 表示落盘失败（调用方不应因此中断轮询）。</summary>
    public bool Save(MailCursor cursor)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(cursor, JsonOptions));
            File.Move(temp, Path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
