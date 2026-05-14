using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AgentLilara.PluginSDK.WebUI;
using Microsoft.AspNetCore.Components;

namespace AgentCoreProcessor.WebUI.Shell;

internal class PageContext : IPageContext, IDisposable
{
    private readonly NavigationManager _nav;
    private readonly ConcurrentDictionary<string, JsonNode?> _state = new();
    private readonly ConcurrentDictionary<string, List<Action<JsonNode?>>> _handlers = new();

    public PageContext(NavigationManager nav)
    {
        _nav = nav;
    }

    public void Emit(string eventName, JsonNode? payload = null)
    {
        if (_handlers.TryGetValue(eventName, out var list))
        {
            foreach (var handler in list.ToArray())
            {
                try { handler(payload); }
                catch { }
            }
        }
    }

    public IDisposable On(string eventName, Action<JsonNode?> handler)
    {
        var list = _handlers.GetOrAdd(eventName, _ => new List<Action<JsonNode?>>());
        lock (list) { list.Add(handler); }
        return new Subscription(() =>
        {
            lock (list) { list.Remove(handler); }
        });
    }

    public JsonNode? GetState(string key)
        => _state.TryGetValue(key, out var val) ? val : null;

    public void SetState(string key, JsonNode? value)
        => _state[key] = value;

    public void Navigate(string route)
        => _nav.NavigateTo("/" + route.TrimStart('/'));

    public void Dispose()
    {
        _handlers.Clear();
        _state.Clear();
    }

    private class Subscription : IDisposable
    {
        private readonly Action _onDispose;
        public Subscription(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }
}
