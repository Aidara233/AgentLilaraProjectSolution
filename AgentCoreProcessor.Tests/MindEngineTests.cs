using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Mind;
using AgentLilara.PluginSDK.Services;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 心智引擎核心链路测试（不依赖 LLM）。
/// 临时 SQLite → DbManager → 各 Repository → Writer/Conflict/Queue/Emotion 全链路。
/// </summary>
public class MindEngineTests
{
    private static string TempDbPath()
        => Path.Combine(Path.GetTempPath(), $"lilara_test_{Guid.NewGuid():N}.db");

    private sealed class TestServices
    {
        public DbManager Db = null!;
        public ClaimRepository Claims = null!;
        public JournalRepository Journals = null!;
        public KeywordRepository Keywords = null!;
        public FragmentKeywordRepository FragKeys = null!;
        public EmotionSnapshotRepository EmotionSnapshots = null!;
        public FrameworkEventRepository FrameworkEvents = null!;
        public ConflictGroupRepository ConflictGroups = null!;
        public ReviewQueueRepository Queue = null!;
        public EvidenceEngine Evidence = null!;
        public ConflictService Conflicts = null!;
        public ReviewQueueService QueueService = null!;
        public EmotionService Emotion = null!;
        public Writer Writer = null!;
        public RetrieveService Retrieve = null!;
    }

    private static async Task<TestServices> CreateServicesAsync()
    {
        var path = TempDbPath();
        var db = new DbManager(path);
        await db.InitAsync();

        var s = new TestServices
        {
            Db = db,
            Claims = new ClaimRepository(db),
            Journals = new JournalRepository(db),
            Keywords = new KeywordRepository(db),
            FragKeys = new FragmentKeywordRepository(db),
            EmotionSnapshots = new EmotionSnapshotRepository(db),
            FrameworkEvents = new FrameworkEventRepository(db),
            ConflictGroups = new ConflictGroupRepository(db),
            Queue = new ReviewQueueRepository(db),
        };
        s.Evidence = new EvidenceEngine(s.Claims, s.ConflictGroups);
        s.Conflicts = new ConflictService(s.Claims, s.ConflictGroups, s.Keywords, s.Evidence);
        s.QueueService = new ReviewQueueService(s.Queue);
        s.Emotion = new EmotionService(s.EmotionSnapshots);
        s.Writer = new Writer(s.Claims, s.Journals, s.Keywords, s.FragKeys,
            s.Conflicts, s.Evidence, s.QueueService, s.Emotion);
        s.Retrieve = new RetrieveService(s.Claims, s.Journals, s.Keywords, s.FragKeys);
        return s;
    }

    // ── 1. Writer 写断言 → Claims + Keywords 关联 ──

    [Fact]
    public async Task WriteClaim_PersistsClaimAndKeyword()
    {
        var s = await CreateServicesAsync();

        var batch = new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest>
            {
                new()
                {
                    Subject = new ClaimSlot { Type = "entity", Index = "咪咪" },
                    Predicate = new ClaimSlot { Type = "relation", Index = "喜欢" },
                    Object = new ClaimSlot { Type = "keyword", Index = "猫粮" },
                    Raw = "咪咪喜欢猫粮",
                    Credibility = 1.0f,
                    UtteranceId = "u_001",
                },
            },
        };

        await s.Writer.WriteBatchAsync(batch);

        var claims = await s.Claims.GetAllAsync();
        Assert.Single(claims);
        Assert.Equal("咪咪", ClaimSlot.FromJson(claims[0].SubjectJson)!.Index);
        Assert.Equal("u_001", claims[0].UtteranceId);

        // 关键词关联：咪咪 + 猫粮 → Keywords 表两条 + FragmentKeywords 两条
        var keywords = await s.Keywords.GetAllAsync();
        Assert.Equal(2, keywords.Count);
        var frags = await s.Db.GetAllAsync<FragmentKeyword>();
        Assert.Equal(2, frags.Count);

        await s.Db.DeleteAsync(claims[0]);
    }

    // ── 2. Writer 写经历 + 情绪信号应用 ──

    [Fact]
    public async Task WriteJournal_AppliesEmotionSignals()
    {
        var s = await CreateServicesAsync();

        var batch = new WriteBatchRequest
        {
            Journals = new List<WriteJournalRequest>
            {
                new()
                {
                    Content = "用户今天陪我聊了很久",
                    Kind = "Experience",
                    Source = "Session",
                    EmotionSignalsJson = "[{\"Axis\":\"joy\",\"Target\":0.8,\"Reason\":\"陪伴\"}]",
                },
            },
        };

        await s.Writer.WriteBatchAsync(batch);

        var journals = await s.Journals.GetAllAsync();
        Assert.Single(journals);

        // 情绪被应用：joy 从 0 → 趋近 0.8
        var state = await s.Emotion.GetCurrentAsync();
        Assert.InRange(state.Joy, 0.1f, 0.8f);

        // 快照落库
        var snaps = await s.EmotionSnapshots.GetRecentAsync(10);
        Assert.NotEmpty(snaps);
    }

    // ── 3. 冲突检测：排他属性不同值 → 建组 ──

    [Fact]
    public async Task ExclusiveConflict_CreatesGroup()
    {
        var s = await CreateServicesAsync();

        // 先建排他属性关键词：颜色 (IsExclusive=true)
        var color = await s.Keywords.CreateAsync("颜色", "attribute", isExclusive: true);

        // 断言 A：咪咪 颜色 红
        var batch1 = new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest>
            {
                new()
                {
                    Subject = new ClaimSlot { Type = "entity", Index = "咪咪" },
                    Predicate = new ClaimSlot { Type = "attribute", Index = "颜色" },
                    Object = new ClaimSlot { Type = "literal", Index = "红" },
                    Raw = "咪咪的颜色是红",
                    Credibility = 1.0f,
                },
            },
        };
        await s.Writer.WriteBatchAsync(batch1);

        // 断言 B：咪咪 颜色 黄（同槽位不同值 → 冲突建组）
        var batch2 = new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest>
            {
                new()
                {
                    Subject = new ClaimSlot { Type = "entity", Index = "咪咪" },
                    Predicate = new ClaimSlot { Type = "attribute", Index = "颜色" },
                    Object = new ClaimSlot { Type = "literal", Index = "黄" },
                    Raw = "咪咪的颜色是黄",
                    Credibility = 1.0f,
                },
            },
        };
        await s.Writer.WriteBatchAsync(batch2);

        var groups = await s.ConflictGroups.GetActiveAsync();
        Assert.Single(groups);
        Assert.Equal("颜色", groups[0].PredicateKey);

        var members = await s.ConflictGroups.GetMemberClaimIdsAsync(groups[0].Id);
        Assert.Equal(2, members.Count);
    }

    // ── 4. 冲突超预算 → ReviewQueue 入队 ──

    [Fact]
    public async Task OverBudget_EnqueuesReview()
    {
        var s = await CreateServicesAsync();

        await s.Keywords.CreateAsync("颜色", "attribute", isExclusive: true);

        // 两条冲突断言，Credibility 各 0.9 → 组内和 1.8 > 1 → 超预算
        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest>
            {
                new()
                {
                    Subject = new ClaimSlot { Type = "entity", Index = "咪咪" },
                    Predicate = new ClaimSlot { Type = "attribute", Index = "颜色" },
                    Object = new ClaimSlot { Type = "literal", Index = "红" },
                    Credibility = 0.9f,
                },
            },
        });
        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest>
            {
                new()
                {
                    Subject = new ClaimSlot { Type = "entity", Index = "咪咪" },
                    Predicate = new ClaimSlot { Type = "attribute", Index = "颜色" },
                    Object = new ClaimSlot { Type = "literal", Index = "黄" },
                    Credibility = 0.9f,
                },
            },
        });

        // 超预算 → ReviewQueue 入队（Source=Conflict）
        var pending = await s.QueueService.GetPendingAsync(10);
        Assert.NotEmpty(pending);
        Assert.Equal("Conflict", pending[0].Source);
    }

    // ── 5. 检索：命中关键词数优先 + 结果含子证据 ──

    [Fact]
    public async Task Retrieve_ReturnsHitsSortedByMatch()
    {
        var s = await CreateServicesAsync();

        await s.Writer.WriteBatchAsync(new WriteBatchRequest
        {
            Claims = new List<WriteClaimRequest>
            {
                new()
                {
                    Subject = new ClaimSlot { Type = "entity", Index = "咪咪" },
                    Predicate = new ClaimSlot { Type = "relation", Index = "喜欢" },
                    Object = new ClaimSlot { Type = "keyword", Index = "猫粮" },
                    Raw = "咪咪喜欢猫粮",
                    Credibility = 0.8f,
                },
            },
        });

        var result = await s.Retrieve.SearchAsync("咪咪 猫粮", limit: 5);
        Assert.NotEmpty(result.Hits);
        Assert.Contains(result.Hits, h => h.Raw != null && h.Raw.Contains("咪咪"));
    }
}
