using System.Runtime.CompilerServices;

// Older WorkingTools binaries reference these names in AgentCoreProcessor.
// Forward them to the single SDK definition so plugin and host share type identity.
[assembly: TypeForwardedTo(typeof(AgentCoreProcessor.Engine.IInjectProvider))]
[assembly: TypeForwardedTo(typeof(AgentCoreProcessor.Engine.InjectContext))]
