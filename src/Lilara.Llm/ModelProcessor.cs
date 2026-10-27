using AgentCoreProcessor.Client;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Core;

/// <summary>Owns model configuration, client cache, prompt assembly and one streaming retry.</summary>
internal class ModelProcessor
{
    private readonly string _configurationDirectory;
    private readonly Func<string, string?> _loadPrompt;
    private readonly Action<string, object>? _warn;
    private readonly Func<ApiClientCfg, IModelClient> _createClient;
    private readonly Dictionary<string, (ApiClientCfg Config, IModelClient Client)> _cache = new();
    private IModelClient _client;
    private string _configurationName = "Base";

    public string? BasePrompt { get; private set; }
    public IModelClient Client => _client;

    public ModelProcessor(string configurationDirectory, Func<string, string?> loadPrompt,
        string? requestDumpDirectory = null, Action<string, object>? warn = null,
        Func<ApiClientCfg, IModelClient>? createClient = null, IModelClient? defaultClient = null,
        string configurationName = "Base")
    {
        _configurationDirectory = configurationDirectory;
        _loadPrompt = loadPrompt;
        _warn = warn;
        _createClient = createClient ?? (configuration => ModelClientFactory.Create(configuration, requestDumpDirectory));
        _client = defaultClient ?? new OpenAIModelClient();
        CfgName = configurationName;
    }

    public string CfgName
    {
        get => _configurationName;
        set
        {
            var path = Path.Combine(_configurationDirectory, value) + ".json";
            if (!File.Exists(path))
            {
                _warn?.Invoke("Core配置文件缺失，回退到Base", new { requested = value });
                _configurationName = "Base";
                return;
            }
            if (_cache.TryGetValue(value, out var cached))
            {
                _configurationName = value;
                _client = cached.Client;
                LoadBasePrompt();
                return;
            }
            _configurationName = value;
            var configuration = ApiClientCfg.FromJson(File.ReadAllText(path));
            _client = _createClient(configuration);
            _cache[value] = (configuration, _client);
            LoadBasePrompt();
        }
    }

    public async Task ProcessAsync(Action<ApiResponse> onDelta, CancellationToken ct = default,
        Action? onRetryReset = null)
    {
        try
        {
            await _client.StreamChatAsync(onDelta, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            onRetryReset?.Invoke();
            await _client.StreamChatAsync(onDelta, ct).ConfigureAwait(false);
        }
    }

    private void LoadBasePrompt()
    {
        var files = _client.Config.PromptFiles;
        if (files == null || files.Count == 0) return;
        var parts = new List<string>();
        foreach (var file in files)
        {
            var content = _loadPrompt(file);
            if (!string.IsNullOrEmpty(content)) parts.Add(content);
        }
        if (parts.Count == 0) return;
        BasePrompt = string.Join("\n\n", parts);
        _client.AddSystemMessage(BasePrompt);
    }
}
