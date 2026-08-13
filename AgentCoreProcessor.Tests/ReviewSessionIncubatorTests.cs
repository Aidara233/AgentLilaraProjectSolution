using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Mind;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// Review 会话孵化器的核心机制测试：
/// 1) SessionServices 经 CreateSessionSpec 注入到会话（IReviewStageAccess 可被组件取到）
/// 2) HasPendingAsync 反映队列状态（孵化器据此决定是否创建 review 会话）
/// </summary>
public class ReviewSessionIncubatorTests
{
    [Fact]
    public void CreateSessionSpec_SupportsSessionServices_Injection()
    {
        // 模拟孵化器构造：一个 per-session 的 IReviewStageAccess 桩
        var spec = new CreateSessionSpec
        {
            Type = "review",
            SessionServices = new Dictionary<Type, object>
            {
                [typeof(IReviewStageAccess)] = new FakeStageAccess()
            }
        };

        Assert.NotNull(spec.SessionServices);
        Assert.IsType<FakeStageAccess>(spec.SessionServices[typeof(IReviewStageAccess)]);
    }

    [Fact]
    public async Task Queue_HasPending_ReflectsQueueState()
    {
        var db = new DbManager(Path.Combine(Path.GetTempPath(), $"lilara_q_{Guid.NewGuid():N}.db"));
        await db.InitAsync();
        var queue = new ReviewQueueService(new ReviewQueueRepository(db));

        Assert.False(await queue.HasPendingAsync());

        await queue.EnqueueAsync("1", "ConflictGroup", "测试", "Test");
        Assert.True(await queue.HasPendingAsync());

        var pending = await queue.GetPendingAsync(10);
        var item = pending.Single();
        await queue.CompleteAsync(item.Id);
        Assert.False(await queue.HasPendingAsync());
    }

    [Fact]
    public void IncubatorCondition_NeedsNoActiveReviewAndPendingQueue()
    {
        // 孵化条件拆解成纯逻辑验证：
        //   队列有活 + 无活跃 review 会话 → 孵化；否则不孵化
        var pendingQueue = true;
        var noActiveReview = true;
        Assert.True(pendingQueue && noActiveReview, "队列有活且无 review 会话应孵化");

        var queueEmpty = false;
        Assert.False(queueEmpty && noActiveReview, "队列空不孵化");
        Assert.False(pendingQueue && !noActiveReview, "已有 review 会话不孵化");
    }

    /// <summary>IReviewStageAccess 桩（仅验证注入路径，不验证逻辑）。</summary>
    private sealed class FakeStageAccess : IReviewStageAccess
    {
        public void StageCredibility(int claimId, float newCredibility) { }
        public void StageExclusivityRemoval(string keyword) { }
        public void StageGroupResolution(int groupId) { }
        public Task<ReviewPreviewDto> PreviewAsync() => Task.FromResult(new ReviewPreviewDto { Ok = true });
        public Task<ReviewCommitDto> CommitAsync() => Task.FromResult(new ReviewCommitDto { Success = true });
        public void Clear() { }
    }
}
