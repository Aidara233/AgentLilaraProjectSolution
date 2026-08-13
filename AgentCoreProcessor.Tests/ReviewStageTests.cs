using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Mind;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// Review 暂存区 + 排他自动标记测试。
/// 覆盖：Writer 写 attribute 断言自动标记 IsExclusive、同槽位不同值自动建冲突组、
/// 暂存区三类操作（可信度/解除排他/拆组）与 commit 落库。
/// </summary>
public class ReviewStageTests
{
    private static string TempDbPath()
        => Path.Combine(Path.GetTempPath(), $"lilara_rev_{Guid.NewGuid():N}.db");

    private sealed class Services
    {
        public DbManager Db = null!;
        public ClaimRepository Claims = null!;
        public JournalRepository Journals = null!;
        public KeywordRepository Keywords = null!;
        public FragmentKeywordRepository FragKeys = null!;
        public EmotionSnapshotRepository EmotionSnaps = null!;
        public ConflictGroupRepository Groups = null!;
        public ReviewQueueRepository Queue = null!;
        public EvidenceEngine Evidence = null!;
        public ConflictService Conflicts = null!;
        public ReviewQueueService QueueService = null!;
        public EmotionService Emotion = null!;
        public Writer Writer = null!;
        public ReviewStage Stage = null!;
    }

    private static async Task<Services> CreateAsync(string? sessionId = null)
    {
        var path = TempDbPath();
        var db = new DbManager(path);
        await db.InitAsync();

        var s = new Services
        {
            Db = db,
            Claims = new ClaimRepository(db),
            Journals = new JournalRepository(db),
            Keywords = new KeywordRepository(db),
            FragKeys = new FragmentKeywordRepository(db),
            EmotionSnaps = new EmotionSnapshotRepository(db),
            Groups = new ConflictGroupRepository(db),
            Queue = new ReviewQueueRepository(db),
        };
        s.Evidence = new EvidenceEngine(s.Claims, s.Groups);
        s.Conflicts = new ConflictService(s.Claims, s.Groups, s.Keywords, s.Evidence);
        s.QueueService = new ReviewQueueService(s.Queue);
        s.Emotion = new EmotionService(s.EmotionSnaps);
        s.Writer = new Writer(s.Claims, s.Journals, s.Keywords, s.FragKeys,
            s.Conflicts, s.Evidence, s.QueueService, s.Emotion);
        s.Stage = new ReviewStage(sessionId ?? Guid.NewGuid().ToString("N"),
            s.Claims, s.Groups, s.Keywords, s.Evidence);
        return s;
    }

    private static WriteClaimRequest Attr(string subject, string attr, string value, float cred = 1.0f)
        => new()
        {
            Subject = new ClaimSlot { Type = "entity", Index = subject },
            Predicate = new ClaimSlot { Type = "attribute", Index = attr },
            Object = new ClaimSlot { Type = "literal", Index = value },
            Raw = $"{subject}的{attr}是{value}",
            Credibility = cred,
        };

    // ── 排他自动标记 ──

    [Fact]
    public async Task Writer_AutoMarksAttributeAsExclusive()
    {
        var s = await CreateAsync();

        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest> { Attr("张三", "职业", "程序员") }
        });

        var kw = await s.Keywords.GetByNameAsync("职业");
        Assert.NotNull(kw);
        Assert.True(kw!.IsExclusive, "attribute 谓词写入时应自动标记 IsExclusive=true");
    }

    [Fact]
    public async Task Writer_DoesNotMarkRelationAsExclusive()
    {
        var s = await CreateAsync();

        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest>
            {
                new()
                {
                    Subject = new ClaimSlot { Type = "entity", Index = "张三" },
                    Predicate = new ClaimSlot { Type = "relation", Index = "喜欢" },
                    Object = new ClaimSlot { Type = "keyword", Index = "猫" },
                    Raw = "张三喜欢猫",
                },
            },
        });

        // relation 谓词不建关键词、更不标记排他（只有 attribute 谓词走排他路径）
        var kw = await s.Keywords.GetByNameAsync("喜欢");
        Assert.Null(kw);
    }

    [Fact]
    public async Task Writer_SameSubjectSameAttrDifferentValue_CreatesConflictGroup()
    {
        var s = await CreateAsync();

        // 无需预建排他关键词——attribute 写入自动标记
        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest> { Attr("张三", "职业", "程序员") }
        });
        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest> { Attr("张三", "职业", "设计师") }
        });

        var groups = await s.Groups.GetActiveAsync();
        Assert.Single(groups);
        Assert.Equal("张三", groups[0].SubjectKey);
        Assert.Equal("职业", groups[0].PredicateKey);
    }

    [Fact]
    public async Task Writer_SameValue_DoesNotConflict()
    {
        var s = await CreateAsync();

        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest> { Attr("张三", "职业", "程序员") }
        });
        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest> { Attr("张三", "职业", "程序员") }
        });

        var groups = await s.Groups.GetActiveAsync();
        Assert.Empty(groups);
    }

    // ── 可信度暂存 ──

    [Fact]
    public async Task Stage_CommitCredibilityChange()
    {
        var s = await CreateAsync();
        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest> { Attr("张三", "职业", "程序员", cred: 0.7f) }
        });
        var claim = (await s.Claims.GetAllAsync()).Single();

        s.Stage.StageCredibility(claim.Id, 0.2f);
        var preview = await s.Stage.PreviewAsync();
        Assert.True(preview.Ok);
        Assert.Contains(claim.Id, preview.AffectedClaims);

        var commit = await s.Stage.CommitAsync();
        Assert.True(commit.Success);
        Assert.Equal(1, commit.CommittedChanges);

        var reloaded = await s.Claims.GetByIdAsync(claim.Id);
        Assert.Equal(0.2f, reloaded!.Credibility, 3);
    }

    // ── 解除排他 ──

    [Fact]
    public async Task Stage_CommitExclusivityRemoval_UnsetsExclusiveFlag()
    {
        var s = await CreateAsync();
        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest> { Attr("张三", "职业", "程序员") }
        });
        // 已自动排他
        var before = await s.Keywords.GetByNameAsync("职业");
        Assert.True(before!.IsExclusive);

        s.Stage.StageExclusivityRemoval("职业");
        var preview = await s.Stage.PreviewAsync();
        Assert.Contains("职业", preview.Message);

        var commit = await s.Stage.CommitAsync();
        Assert.True(commit.Success);

        var after = await s.Keywords.GetByNameAsync("职业");
        Assert.False(after!.IsExclusive, "commit 后属性应解除排他");
    }

    [Fact]
    public async Task Stage_AfterDeclassify_NewConflictValue_NoLongerCreatesGroup()
    {
        var s = await CreateAsync();
        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest> { Attr("张三", "职业", "程序员") }
        });
        var before = await s.Keywords.GetByNameAsync("职业");
        Assert.True(before!.IsExclusive);

        // 解除排他
        s.Stage.StageExclusivityRemoval("职业");
        await s.Stage.CommitAsync();

        // 再写一个不同值，不应再建组
        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest> { Attr("张三", "职业", "设计师") }
        });
        var groups = await s.Groups.GetActiveAsync();
        Assert.Empty(groups);
    }

    // ── 拆解冲突组 ──

    [Fact]
    public async Task Stage_CommitGroupResolution_MarksResolved()
    {
        var s = await CreateAsync();
        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest> { Attr("张三", "职业", "程序员") }
        });
        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest> { Attr("张三", "职业", "设计师") }
        });
        var group = (await s.Groups.GetActiveAsync()).Single();
        Assert.Null(group.ResolvedAt);

        s.Stage.StageGroupResolution(group.Id);
        var preview = await s.Stage.PreviewAsync();
        Assert.Contains(group.Id.ToString(), preview.Message);

        var commit = await s.Stage.CommitAsync();
        Assert.True(commit.Success);

        var reloaded = await s.Groups.GetByIdAsync(group.Id);
        Assert.NotNull(reloaded!.ResolvedAt);
    }

    [Fact]
    public async Task Stage_CommitGroupResolution_ThenNewValueCreatesNewGroup()
    {
        var s = await CreateAsync();
        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest> { Attr("张三", "职业", "程序员") }
        });
        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest> { Attr("张三", "职业", "设计师") }
        });
        var group = (await s.Groups.GetActiveAsync()).Single();

        // 拆解当前组
        s.Stage.StageGroupResolution(group.Id);
        await s.Stage.CommitAsync();

        // 再写一个新值——应建新组（已解决组不复用）
        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest> { Attr("张三", "职业", "工程师") }
        });
        var groups = await s.Groups.GetActiveAsync();
        Assert.Single(groups);
        Assert.NotEqual(group.Id, groups[0].Id);
    }

    // ── 组合操作 ──

    [Fact]
    public async Task Stage_CommitAllThree_Together()
    {
        var s = await CreateAsync();
        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest> { Attr("张三", "职业", "程序员", cred: 0.7f) }
        });
        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest> { Attr("张三", "职业", "设计师", cred: 0.7f) }
        });
        var claim = (await s.Claims.GetAllAsync()).First();
        var group = (await s.Groups.GetActiveAsync()).Single();

        // 三类操作一起暂存
        s.Stage.StageCredibility(claim.Id, 0.2f);
        s.Stage.StageExclusivityRemoval("职业");
        s.Stage.StageGroupResolution(group.Id);

        var preview = await s.Stage.PreviewAsync();
        Assert.True(preview.Ok);
        Assert.Contains("可信度改动 1 条", preview.Message);
        Assert.Contains("解除排他属性 1 个", preview.Message);
        Assert.Contains("拆解冲突组 1 个", preview.Message);

        var commit = await s.Stage.CommitAsync();
        Assert.True(commit.Success);
        Assert.Equal(3, commit.CommittedChanges);
    }

    // ── 空暂存区 ──

    [Fact]
    public async Task Stage_CommitEmpty_Fails()
    {
        var s = await CreateAsync();
        var commit = await s.Stage.CommitAsync();
        Assert.False(commit.Success);
        Assert.Equal("暂存区为空", commit.Error);
    }
}
