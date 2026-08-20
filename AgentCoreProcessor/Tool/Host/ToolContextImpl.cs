using System;
using System.Collections.Concurrent;
using System.IO;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool.Host
{
    /// <summary>
    /// IToolContext 实现。纯服务容器，内部模块注册服务，插件按类型获取。
    /// </summary>
    internal class ToolContextImpl : IToolContext
    {
        private readonly ConcurrentDictionary<Type, object> _services = new();
        private readonly IPluginStorage _storage;

        public ToolContextImpl(IPluginStorage storage)
        {
            _storage = storage;
            Workspace = new WorkspaceAccessImpl(Config.PathConfig.WorkspacePath);
            PluginData = new PluginDataAccessImpl(storage.GlobalDirectory, storage.InstanceDirectory);
        }

        public IPluginStorage Storage => _storage;

        public IWorkspaceAccess Workspace { get; }

        public IPluginDataAccess PluginData { get; }

        /// <summary>注册服务实例。重复注册同类型会覆盖。</summary>
        public void Register<T>(T service) where T : class
        {
            _services[typeof(T)] = service;
        }

        /// <summary>注销服务。</summary>
        public void Unregister<T>() where T : class
        {
            _services.TryRemove(typeof(T), out _);
        }

        /// <summary>检查服务是否已注册。</summary>
        public bool Has<T>() where T : class
        {
            return _services.ContainsKey(typeof(T));
        }

        public T? GetService<T>() where T : class
        {
            return _services.TryGetValue(typeof(T), out var svc) ? (T)svc : null;
        }
    }

    /// <summary>
    /// IPluginStorage 实现。根据插件名和作用域提供隔离目录。
    /// </summary>
    internal class PluginStorageImpl : IPluginStorage
    {
        public string GlobalDirectory { get; }
        public string InstanceDirectory { get; }
        public string WorkspaceDirectory => Config.PathConfig.WorkspacePath;

        public PluginStorageImpl(string pluginName, string? instanceId = null)
        {
            var baseDir = Path.Combine(Config.PathConfig.StoragePath, "PluginData", pluginName);
            GlobalDirectory = baseDir;
            Directory.CreateDirectory(GlobalDirectory);

            if (instanceId != null)
            {
                InstanceDirectory = Path.Combine(baseDir, "instances", instanceId);
                Directory.CreateDirectory(InstanceDirectory);
            }
            else
            {
                InstanceDirectory = GlobalDirectory;
            }
        }
    }

    /// <summary>IWorkspaceAccess 实现。Workspace/ 目录沙箱，越界返回 null。</summary>
    internal class WorkspaceAccessImpl : IWorkspaceAccess
    {
        private readonly string _root;

        public WorkspaceAccessImpl(string workspaceDir)
        {
            _root = Path.GetFullPath(workspaceDir);
            Directory.CreateDirectory(_root);
        }

        public string? Resolve(string path)
        {
            if (string.IsNullOrEmpty(path)) return _root;
            string full = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(_root, path));
            return IsInside(full) ? full : null;
        }

        public async Task<string?> ReadAllTextAsync(string path, CancellationToken ct = default)
        {
            var full = Resolve(path);
            if (full == null || !File.Exists(full)) return null;
            return await File.ReadAllTextAsync(full, ct).ConfigureAwait(false);
        }

        public async Task WriteAllTextAsync(string path, string content, CancellationToken ct = default)
        {
            var full = Resolve(path)
                ?? throw new InvalidOperationException($"路径越界：{path}（只允许 Workspace 内文件）");
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(full, content, ct).ConfigureAwait(false);
        }

        private bool IsInside(string full)
        {
            var root = _root.EndsWith(Path.DirectorySeparatorChar)
                ? _root : _root + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || full.Equals(_root, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>IPluginDataAccess 实现。插件数据目录沙箱，越界返回 null。</summary>
    internal class PluginDataAccessImpl : IPluginDataAccess
    {
        private readonly WorkspaceAccessImpl _global;
        private readonly WorkspaceAccessImpl _instance;

        public PluginDataAccessImpl(string globalDirectory, string instanceDirectory)
        {
            _global = new WorkspaceAccessImpl(globalDirectory);
            _instance = new WorkspaceAccessImpl(instanceDirectory);
        }

        public string? ResolveGlobal(string path) => _global.Resolve(path);
        public string? ResolveInstance(string path) => _instance.Resolve(path);
    }
}
