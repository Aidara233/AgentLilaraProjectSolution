namespace Plugin.GitTools;

public static class GitToolsAccessor
{
    public static GitGlobalComponent? Global { get; private set; }
    public static void Configure(GitGlobalComponent global) => Global = global;
    public static void Clear() => Global = null;
}
