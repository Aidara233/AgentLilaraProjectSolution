// AgentCoreProcessor.Tests/AssemblyInfo.cs
using Xunit;

// 测试类间共享静态状态（PathConfig.StoragePath、SessionTypeProfiles 缓存），
// 必须串行执行，否则并发互踩导致随机失败。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
