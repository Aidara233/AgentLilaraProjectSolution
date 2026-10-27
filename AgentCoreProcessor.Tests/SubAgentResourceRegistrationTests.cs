using AgentCoreProcessor.Component;
using AgentLilara.PluginSDK.Services;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class SubAgentResourceRegistrationTests
{
    private sealed class Access(string name, List<string> events) : ISessionResourceAccess
    {
        public HashSet<ISessionResource> Resources { get; } = new();
        public void Register(ISessionResource resource) { events.Add(name + ":register"); Resources.Add(resource); }
        public void Unregister(ISessionResource resource) { events.Add(name + ":unregister"); Resources.Remove(resource); }
        public string? ResolveActiveSession(string id) => id;
    }

    [Fact]
    public void SamePortIsRegisteredOnlyOnce()
    {
        var events = new List<string>();
        var port = new Access("one", events);
        var adapter = new SubAgentAccessAdapter(null!, () => port);
        adapter.EnsureResourceRegistered();
        adapter.EnsureResourceRegistered();
        Assert.Equal(new[] { "one:register" }, events);
        Assert.Contains(adapter, port.Resources);
    }

    [Fact]
    public void ReplacementUnregistersOldPortBeforeRegisteringNewPort()
    {
        var events = new List<string>();
        var first = new Access("one", events);
        var second = new Access("two", events);
        ISessionResourceAccess? current = first;
        var adapter = new SubAgentAccessAdapter(null!, () => current);
        adapter.EnsureResourceRegistered();
        current = second;
        adapter.EnsureResourceRegistered();
        adapter.EnsureResourceRegistered();
        Assert.Equal(new[] { "one:register", "one:unregister", "two:register" }, events);
        Assert.Empty(first.Resources);
        Assert.Contains(adapter, second.Resources);
    }

    [Fact]
    public void MissingServiceClearsBindingAndReturningServiceRegistersAgain()
    {
        var events = new List<string>();
        var port = new Access("one", events);
        ISessionResourceAccess? current = null;
        var adapter = new SubAgentAccessAdapter(null!, () => current);
        adapter.EnsureResourceRegistered();
        current = port;
        adapter.EnsureResourceRegistered();
        current = null;
        adapter.EnsureResourceRegistered();
        adapter.EnsureResourceRegistered();
        Assert.Empty(port.Resources);
        current = port;
        adapter.EnsureResourceRegistered();
        Assert.Equal(new[] { "one:register", "one:unregister", "one:register" }, events);
    }
}
