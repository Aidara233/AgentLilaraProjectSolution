using AgentCoreProcessor.Config;
using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>宿主兼容入口；配置、规则和缓存归 Lilara.Session 的实例存储所有。</summary>
internal static class SessionTypeProfiles
{
    private static readonly object Gate = new();
    private static string? _directory;
    private static SessionTypeProfileStore? _store;

    private static SessionTypeProfileStore Store
    {
        get
        {
            var directory = Path.Combine(PathConfig.StoragePath, "SessionTypes");
            lock (Gate)
            {
                if (_store is null || !string.Equals(directory, _directory, StringComparison.Ordinal))
                {
                    _directory = directory;
                    _store = new SessionTypeProfileStore(directory,
                        (message, detail) => Signal.Warn(LogGroup.Engine, message, detail));
                }
                return _store;
            }
        }
    }

    public static SessionTypeProfile Get(string type) => Store.Get(type);
    public static void Invalidate() => Store.Invalidate();
    public static IReadOnlyList<string> All() => Store.All();
    public static bool IsAllowed(string? type, string toolName) => Store.IsAllowed(type, toolName);
    public static IReadOnlyList<string> GetTools(string type) => Store.GetTools(type);
    public static string GetMode(string type) => Store.GetMode(type);
    public static (string Mode, string[] Tools) GetDefault(string type) => Store.GetDefault(type);
    public static void Save(string type, string mode, IEnumerable<string> tools) => Store.Save(type, mode, tools);
    public static void SaveComponents(string type, IEnumerable<string> components) => Store.SaveComponents(type, components);
}
