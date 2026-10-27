using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Session.Brain;
using AgentCoreProcessor.Models;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Core;

/// <summary>Host binding for the concrete legacy core and its immediately-following shadow observation.</summary>
internal sealed class AgentCoreModelAdapter : IAgentModel
{
    private readonly AgentCore _core;
    private readonly AgentModelBinding _binding;

    public AgentCoreModelAdapter(AgentModelBinding binding)
    {
        _binding = binding;
        _core = new AgentCore(binding.ConfigurationName)
        {
            CallerTag = binding.SessionType == "task" ? $"Task:{binding.SessionId}" : $"Session:{binding.SessionId}"
        };
    }

    public bool UseNativeTools => _core.UseNativeTools;
    public bool DisableImageMessages => _core.DisableImageMessages;
    public string? SessionPrompt { set => _core.SessionPrompt = value; }
    public void ConfigureDefaults(string engineType, IReadOnlyList<ITool> tools)
    {
        _core.EngineType = engineType;
        _core.AdditionalTools = tools.ToList();
    }

    public async Task<ModelOutput> InvokeAsync(List<Message> messages, AgentInvokeRequest request)
    {
        var wire = Client.WireRequestCapture.Begin();
        var output = await _core.InvokeAsync(messages, EngineMode.Working, request);
        // Observe before a subsequent call can reset the client's actual request history.
        SessionBrainRuntime.Observe(_core, _binding.SessionId, request.EngineType ?? _binding.SessionType,
            PathConfig.StoragePath, wire.Body);
        return output;
    }
}
