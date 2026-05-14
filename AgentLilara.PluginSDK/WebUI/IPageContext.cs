using System;
using System.Text.Json.Nodes;

namespace AgentLilara.PluginSDK.WebUI;

public interface IPageContext
{
    void Emit(string eventName, JsonNode? payload = null);
    IDisposable On(string eventName, Action<JsonNode?> handler);
    JsonNode? GetState(string key);
    void SetState(string key, JsonNode? value);
    void Navigate(string route);
}
