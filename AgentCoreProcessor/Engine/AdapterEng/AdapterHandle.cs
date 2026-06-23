// AgentCoreProcessor/Engine/AdapterEng/AdapterHandle.cs
namespace AgentCoreProcessor.Engine.AdapterEng;

internal class AdapterHandle
{
    public int Priority { get; set; }
    public string AdapterId { get; set; } = "";
    public bool IsAlive { get; set; } = true;
    public Adapter.IAdapter Adapter { get; set; } = null!;
}
