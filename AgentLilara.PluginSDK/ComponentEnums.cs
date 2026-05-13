// AgentLilara.PluginSDK/ComponentEnums.cs
namespace AgentLilara.PluginSDK;

public enum ComponentScope { Global, Loop }
public enum Applicability { Enabled, Disabled, NotApplicable }
public enum Visibility { AlwaysVisible, FollowState, AlwaysHidden }
public enum ShutdownReason { Destroy, Reload }
public enum InitReason { Fresh, Reload }
