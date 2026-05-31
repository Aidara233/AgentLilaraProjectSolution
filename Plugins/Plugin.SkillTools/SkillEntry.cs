namespace Plugin.SkillTools;

/// <summary>
/// 解析后的 Skill 元数据。
/// </summary>
public class SkillEntry
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string? Engines { get; init; }
    public required string DirectoryPath { get; init; }

    /// <summary>主文件路径（SKILL.md）。</summary>
    public string MainFilePath => Path.Combine(DirectoryPath, "SKILL.md");

    /// <summary>获取正文（去掉 frontmatter）。</summary>
    public string GetBody()
    {
        var text = File.ReadAllText(MainFilePath);
        return StripFrontmatter(text);
    }

    /// <summary>检查此 skill 是否对指定引擎类型可用。</summary>
    public bool IsAvailableFor(string engineType)
    {
        if (string.IsNullOrWhiteSpace(Engines))
            return true;

        var parts = Engines.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => p.Equals(engineType, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>列出目录中的辅助文件（排除 SKILL.md）。</summary>
    public IReadOnlyList<string> ListFiles()
    {
        if (!Directory.Exists(DirectoryPath))
            return Array.Empty<string>();

        return Directory.GetFiles(DirectoryPath)
            .Select(Path.GetFileName)
            .Where(f => !string.Equals(f, "SKILL.md", StringComparison.OrdinalIgnoreCase))
            .ToList()!;
    }

    /// <summary>读取指定辅助文件的内容。</summary>
    public string? ReadFile(string fileName)
    {
        var path = Path.Combine(DirectoryPath, fileName);
        if (!File.Exists(path)) return null;

        // 防止目录遍历
        var fullPath = Path.GetFullPath(path);
        var dirRoot = Path.GetFullPath(DirectoryPath);
        var dirRootWithSep = dirRoot.EndsWith(Path.DirectorySeparatorChar)
            ? dirRoot : dirRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(dirRootWithSep, StringComparison.OrdinalIgnoreCase)
            && !fullPath.Equals(dirRoot, StringComparison.OrdinalIgnoreCase))
            return null;

        return File.ReadAllText(fullPath);
    }

    /// <summary>从 SKILL.md 文件解析 frontmatter 并构建 SkillEntry。</summary>
    public static SkillEntry? Parse(string skillDirectory)
    {
        var mainFile = Path.Combine(skillDirectory, "SKILL.md");
        if (!File.Exists(mainFile)) return null;

        var text = File.ReadAllText(mainFile);
        var (name, description, engines) = ParseFrontmatter(text);

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description))
            return null;

        return new SkillEntry
        {
            Name = name,
            Description = description,
            Engines = engines,
            DirectoryPath = skillDirectory
        };
    }

    private static (string? name, string? description, string? engines) ParseFrontmatter(string text)
    {
        if (!text.StartsWith("---"))
            return (null, null, null);

        var end = text.IndexOf("---", 3);
        if (end < 0) return (null, null, null);

        var frontmatter = text[3..end];
        string? name = null, description = null, engines = null;

        foreach (var line in frontmatter.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx < 0) continue;

            var key = trimmed[..colonIdx].Trim().ToLower();
            var value = trimmed[(colonIdx + 1)..].Trim().Trim('"');

            switch (key)
            {
                case "name": name = value; break;
                case "description": description = value; break;
                case "engines": engines = value; break;
            }
        }

        return (name, description, engines);
    }

    private static string StripFrontmatter(string text)
    {
        if (!text.StartsWith("---"))
            return text;

        var end = text.IndexOf("---", 3);
        if (end < 0) return text;

        return text[(end + 3)..].TrimStart('\n', '\r');
    }
}
