using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Mind;
using AgentCoreProcessor.Tool.Host;
using AgentLilara.PluginSDK.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 伴随提取器测试（LLM 用桩替换，不打真实模型）。
/// 覆盖：语义槽位解析、可信度夹取、残缺条目丢弃、非 JSON 不兜底、攒批与空闲触发。
/// </summary>
public class ExpressExtractorTests
{
    /// <summary>可编程 LLM 桩：按序返回预设输出，记录收到的 context。</summary>
    private sealed class FakeLlm : IExtractionLlm
    {
        private readonly Queue<string?> _outputs = new();
        public List<string> Contexts { get; } = new();
        public int CallCount => Contexts.Count;

        public FakeLlm(params string?[] outputs)
        {
            foreach (var o in outputs) _outputs.Enqueue(o);
        }

        public Task<string?> ExtractClaimsAsync(string context)
        {
            Contexts.Add(context);
            return Task.FromResult(_outputs.Count > 0 ? _outputs.Dequeue() : null);
        }
    }

    private sealed class Harness
    {
        public ClaimRepository Claims = null!;
        public KeywordRepository Keywords = null!;
        public FakeLlm Llm = null!;
        public ExpressExtractor Extractor = null!;
    }

    private static async Task<Harness> CreateAsync(IExtractionLlm llm, int batchSize = 20)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lilara_extract_{Guid.NewGuid():N}.db");
        var db = new DbManager(path);
        await db.InitAsync();

        var claims = new ClaimRepository(db);
        var journals = new JournalRepository(db);
        var keywords = new KeywordRepository(db);
        var fragKeys = new FragmentKeywordRepository(db);
        var emotionSnaps = new EmotionSnapshotRepository(db);
        var groups = new ConflictGroupRepository(db);
        var queue = new ReviewQueueRepository(db);

        var evidence = new EvidenceEngine(claims, groups);
        var conflicts = new ConflictService(claims, groups, keywords, evidence);
        var writer = new Writer(claims, journals, keywords, fragKeys,
            conflicts, evidence, new ReviewQueueService(queue), new EmotionService(emotionSnaps));

        return new Harness
        {
            Claims = claims,
            Keywords = keywords,
            Llm = llm as FakeLlm ?? new FakeLlm(),
            Extractor = new ExpressExtractor(new WriterAccessImpl(writer), llm, batchSize),
        };
    }

    // ── 语义槽位提取 ──

    [Fact]
    public async Task Extract_ParsesSemanticSlots_AndPersists()
    {
        var llm = new FakeLlm("""
        {"claims":[
          {"msg":0,
           "subject":{"type":"entity","index":"咪咪"},
           "predicate":{"type":"relation","index":"喜欢"},
           "object":{"type":"keyword","index":"三文鱼"},
           "raw":"咪咪喜欢吃三文鱼","strength":0.8,"credibility":0.7,"inferred":false}
        ]}
        """);
        var h = await CreateAsync(llm, batchSize: 1);

        await h.Extractor.AddMessageAsync("13", "张三", "我家咪咪特别喜欢吃三文鱼");

        var all = await h.Claims.GetAllAsync();
        var claim = Assert.Single(all);

        Assert.Equal("entity", ClaimSlot.FromJson(claim.SubjectJson)!.Type);
        Assert.Equal("咪咪", ClaimSlot.FromJson(claim.SubjectJson)!.Index);
        Assert.Equal("relation", ClaimSlot.FromJson(claim.PredicateJson)!.Type);
        Assert.Equal("喜欢", ClaimSlot.FromJson(claim.PredicateJson)!.Index);
        Assert.Equal("keyword", ClaimSlot.FromJson(claim.ObjectJson)!.Type);
        Assert.Equal("三文鱼", ClaimSlot.FromJson(claim.ObjectJson)!.Index);

        Assert.Equal("咪咪喜欢吃三文鱼", claim.Raw);
        Assert.Equal(0.8f, claim.Strength!.Value, 3);
        Assert.Equal(0.7f, claim.Credibility, 3);
        Assert.False(claim.IsInference);
    }

    [Fact]
    public async Task Extract_LinksKeywords_ForNonLiteralSlots()
    {
        var llm = new FakeLlm("""
        {"claims":[{"msg":0,
          "subject":{"type":"entity","index":"咪咪"},
          "predicate":{"type":"relation","index":"喜欢"},
          "object":{"type":"keyword","index":"三文鱼"},
          "raw":"咪咪喜欢三文鱼"}]}
        """);
        var h = await CreateAsync(llm, batchSize: 1);

        await h.Extractor.AddMessageAsync("13", "张三", "咪咪喜欢三文鱼");

        // subject(entity) + object(keyword) 都应建关键词，literal 才跳过
        var kw = await h.Keywords.GetAllAsync();
        Assert.Contains(kw, k => k.Name == "咪咪");
        Assert.Contains(kw, k => k.Name == "三文鱼");
    }

    // ── 可信度 ──

    [Fact]
    public async Task Extract_DefaultsCredibilityTo07_WhenAbsent()
    {
        var llm = new FakeLlm("""
        {"claims":[{"msg":0,
          "subject":{"type":"entity","index":"张三"},
          "predicate":{"type":"attribute","index":"职业"},
          "object":{"type":"attribute","index":"程序员"},
          "raw":"张三是程序员"}]}
        """);
        var h = await CreateAsync(llm, batchSize: 1);

        await h.Extractor.AddMessageAsync("13", "张三", "我是程序员");

        var claim = Assert.Single(await h.Claims.GetAllAsync());
        Assert.Equal(0.7f, claim.Credibility, 3);
    }

    [Theory]
    [InlineData(1.0, 0.95)]   // 上限夹取：留证据聚合调节空间
    [InlineData(0.0, 0.05)]   // 下限夹取
    [InlineData(0.3, 0.30)]   // 区间内原值保留
    public async Task Extract_ClampsCredibility(double given, double expected)
    {
        var llm = new FakeLlm($$"""
        {"claims":[{"msg":0,
          "subject":{"type":"entity","index":"张三"},
          "predicate":{"type":"relation","index":"养"},
          "object":{"type":"keyword","index":"猫"},
          "raw":"张三养猫","credibility":{{given}}}]}
        """);
        var h = await CreateAsync(llm, batchSize: 1);

        await h.Extractor.AddMessageAsync("13", "张三", "我养猫");

        var claim = Assert.Single(await h.Claims.GetAllAsync());
        Assert.Equal((float)expected, claim.Credibility, 3);
    }

    [Fact]
    public async Task Extract_ClampsStrengthToRange()
    {
        var llm = new FakeLlm("""
        {"claims":[{"msg":0,
          "subject":{"type":"entity","index":"张三"},
          "predicate":{"type":"relation","index":"讨厌"},
          "object":{"type":"keyword","index":"香菜"},
          "raw":"张三讨厌香菜","strength":-5.0}]}
        """);
        var h = await CreateAsync(llm, batchSize: 1);

        await h.Extractor.AddMessageAsync("13", "张三", "我最讨厌香菜");

        var claim = Assert.Single(await h.Claims.GetAllAsync());
        Assert.Equal(-1.0f, claim.Strength!.Value, 3);
    }

    // ── 丢弃规则（不兜底） ──

    [Fact]
    public async Task Extract_DropsClaim_WhenSlotMissing()
    {
        // 缺 object → 整条丢弃，不补窄断言
        var llm = new FakeLlm("""
        {"claims":[{"msg":0,
          "subject":{"type":"entity","index":"张三"},
          "predicate":{"type":"relation","index":"喜欢"},
          "raw":"张三喜欢"}]}
        """);
        var h = await CreateAsync(llm, batchSize: 1);

        await h.Extractor.AddMessageAsync("13", "张三", "我喜欢");

        Assert.Empty(await h.Claims.GetAllAsync());
    }

    [Fact]
    public async Task Extract_DropsClaim_WhenMessageIndexOutOfRange()
    {
        var llm = new FakeLlm("""
        {"claims":[{"msg":99,
          "subject":{"type":"entity","index":"张三"},
          "predicate":{"type":"relation","index":"养"},
          "object":{"type":"keyword","index":"猫"},
          "raw":"张三养猫"}]}
        """);
        var h = await CreateAsync(llm, batchSize: 1);

        await h.Extractor.AddMessageAsync("13", "张三", "我养猫");

        Assert.Empty(await h.Claims.GetAllAsync());
    }

    [Fact]
    public async Task Extract_WritesNothing_WhenOutputNotJson()
    {
        // 非 JSON 不再兜底造窄断言（旧行为会灌整批）
        var llm = new FakeLlm("好的，我明白了，这段对话里没什么特别的。");
        var h = await CreateAsync(llm, batchSize: 1);

        await h.Extractor.AddMessageAsync("13", "张三", "我养猫");

        Assert.Empty(await h.Claims.GetAllAsync());
    }

    [Fact]
    public async Task Extract_WritesNothing_WhenClaimsEmpty()
    {
        var llm = new FakeLlm("""{"claims":[]}""");
        var h = await CreateAsync(llm, batchSize: 1);

        await h.Extractor.AddMessageAsync("13", "张三", "哈哈哈");

        Assert.Empty(await h.Claims.GetAllAsync());
    }

    [Fact]
    public async Task Extract_StripsMarkdownFence()
    {
        var llm = new FakeLlm("""
        ```json
        {"claims":[{"msg":0,
          "subject":{"type":"entity","index":"张三"},
          "predicate":{"type":"relation","index":"养"},
          "object":{"type":"keyword","index":"猫"},
          "raw":"张三养猫"}]}
        ```
        """);
        var h = await CreateAsync(llm, batchSize: 1);

        await h.Extractor.AddMessageAsync("13", "张三", "我养猫");

        Assert.Single(await h.Claims.GetAllAsync());
    }

    // ── 类型纠正 ──

    [Fact]
    public async Task Extract_CorrectsInvalidSlotType()
    {
        // type 非法 → 纠正为 keyword；predicate 非法 → 纠正为 relation
        var llm = new FakeLlm("""
        {"claims":[{"msg":0,
          "subject":{"type":"person","index":"张三"},
          "predicate":{"type":"verb","index":"养"},
          "object":{"type":"thing","index":"猫"},
          "raw":"张三养猫"}]}
        """);
        var h = await CreateAsync(llm, batchSize: 1);

        await h.Extractor.AddMessageAsync("13", "张三", "我养猫");

        var claim = Assert.Single(await h.Claims.GetAllAsync());
        Assert.Equal("keyword", ClaimSlot.FromJson(claim.SubjectJson)!.Type);
        Assert.Equal("relation", ClaimSlot.FromJson(claim.PredicateJson)!.Type);
        Assert.Equal("keyword", ClaimSlot.FromJson(claim.ObjectJson)!.Type);
    }

    // ── 同源分组 ──

    [Fact]
    public async Task Extract_SharesUtteranceId_ForClaimsFromSameMessage()
    {
        var llm = new FakeLlm("""
        {"claims":[
          {"msg":0,"subject":{"type":"entity","index":"咪咪"},
           "predicate":{"type":"attribute","index":"种类"},
           "object":{"type":"keyword","index":"猫"},"raw":"咪咪是猫"},
          {"msg":0,"subject":{"type":"entity","index":"咪咪"},
           "predicate":{"type":"relation","index":"喜欢"},
           "object":{"type":"keyword","index":"三文鱼"},"raw":"咪咪喜欢三文鱼"}
        ]}
        """);
        var h = await CreateAsync(llm, batchSize: 1);

        await h.Extractor.AddMessageAsync("13", "张三", "我家猫咪咪喜欢三文鱼");

        var all = await h.Claims.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.NotNull(all[0].UtteranceId);
        Assert.Equal(all[0].UtteranceId, all[1].UtteranceId);
    }

    [Fact]
    public async Task Extract_BuildsSourceReference()
    {
        var llm = new FakeLlm("""
        {"claims":[{"msg":0,
          "subject":{"type":"entity","index":"张三"},
          "predicate":{"type":"relation","index":"养"},
          "object":{"type":"keyword","index":"猫"},
          "raw":"张三养猫"}]}
        """);
        var h = await CreateAsync(llm, batchSize: 1);

        await h.Extractor.AddMessageAsync("42", "张三", "我养猫");

        var claim = Assert.Single(await h.Claims.GetAllAsync());
        var source = JObject.Parse(claim.SourceJson);
        Assert.Equal("张三", source["who"]!.ToString());
        Assert.Equal("message", source["kind"]!.ToString());
        Assert.Equal("42", source["channel"]!.ToString());
        Assert.Equal("message", source["ref"]!["kind"]!.ToString());
    }

    // ── 攒批与触发 ──

    [Fact]
    public async Task AddMessage_DoesNotCallLlm_BeforeBatchSizeReached()
    {
        var llm = new FakeLlm("""{"claims":[]}""");
        var h = await CreateAsync(llm, batchSize: 5);

        for (int i = 0; i < 4; i++)
            await h.Extractor.AddMessageAsync("13", "张三", $"消息{i}");

        Assert.Equal(0, llm.CallCount);
        Assert.Equal(4, h.Extractor.PendingCount);
    }

    [Fact]
    public async Task AddMessage_TriggersOnce_WhenBatchSizeReached()
    {
        var llm = new FakeLlm("""{"claims":[]}""");
        var h = await CreateAsync(llm, batchSize: 5);

        for (int i = 0; i < 5; i++)
            await h.Extractor.AddMessageAsync("13", "张三", $"消息{i}");

        Assert.Equal(1, llm.CallCount);
        Assert.Equal(0, h.Extractor.PendingCount);
    }

    [Fact]
    public async Task Context_IndexesMessages_AndOmitsInstructions()
    {
        // 指令在 ExtractionPrompt.txt，context 只含消息列表
        var llm = new FakeLlm("""{"claims":[]}""");
        var h = await CreateAsync(llm, batchSize: 2);

        await h.Extractor.AddMessageAsync("13", "张三", "第一句");
        await h.Extractor.AddMessageAsync("13", "Lilara", "第二句");

        var ctx = Assert.Single(llm.Contexts);
        Assert.Contains("[0] <张三> 第一句", ctx);
        Assert.Contains("[1] <Lilara> 第二句", ctx);
        Assert.DoesNotContain("JSON", ctx);
    }

    [Fact]
    public async Task TriggerIdle_SkipsWhenPendingBelowMinimum()
    {
        var llm = new FakeLlm("""{"claims":[]}""");
        var h = await CreateAsync(llm, batchSize: 20);

        await h.Extractor.AddMessageAsync("13", "张三", "只有一条");
        await h.Extractor.TriggerIdleAsync(idleSeconds: 0);

        Assert.Equal(0, llm.CallCount);
        Assert.Equal(1, h.Extractor.PendingCount);
    }

    [Fact]
    public async Task TriggerIdle_SkipsWhenNotIdleLongEnough()
    {
        var llm = new FakeLlm("""{"claims":[]}""");
        var h = await CreateAsync(llm, batchSize: 20);

        for (int i = 0; i < 5; i++)
            await h.Extractor.AddMessageAsync("13", "张三", $"消息{i}");

        // 刚入批，静默时长不足
        await h.Extractor.TriggerIdleAsync(idleSeconds: 300);

        Assert.Equal(0, llm.CallCount);
        Assert.Equal(5, h.Extractor.PendingCount);
    }

    [Fact]
    public async Task TriggerIdle_FlushesTail_WhenIdleAndAboveMinimum()
    {
        var llm = new FakeLlm("""{"claims":[]}""");
        var h = await CreateAsync(llm, batchSize: 20);

        for (int i = 0; i < 5; i++)
            await h.Extractor.AddMessageAsync("13", "张三", $"消息{i}");

        await h.Extractor.TriggerIdleAsync(idleSeconds: 0);

        Assert.Equal(1, llm.CallCount);
        Assert.Equal(0, h.Extractor.PendingCount);
    }

    [Fact]
    public async Task FlushAll_IgnoresThresholds()
    {
        var llm = new FakeLlm("""{"claims":[]}""");
        var h = await CreateAsync(llm, batchSize: 20);

        await h.Extractor.AddMessageAsync("13", "张三", "只有一条");
        await h.Extractor.FlushAllAsync();

        Assert.Equal(1, llm.CallCount);
        Assert.Equal(0, h.Extractor.PendingCount);
    }

    [Fact]
    public async Task ExtractCount_IncrementsOnlyOnSuccessfulWrite()
    {
        var llm = new FakeLlm(
            """{"claims":[]}""",                       // 无产出 → 不计数
            """
            {"claims":[{"msg":0,
              "subject":{"type":"entity","index":"张三"},
              "predicate":{"type":"relation","index":"养"},
              "object":{"type":"keyword","index":"猫"},
              "raw":"张三养猫"}]}
            """);
        var h = await CreateAsync(llm, batchSize: 1);

        await h.Extractor.AddMessageAsync("13", "张三", "哈哈");
        Assert.Equal(0, h.Extractor.ExtractCount);

        await h.Extractor.AddMessageAsync("13", "张三", "我养猫");
        Assert.Equal(1, h.Extractor.ExtractCount);
    }

    [Fact]
    public async Task Extract_SwallowsLlmException_WithoutBlocking()
    {
        var h = await CreateAsync(new ThrowingLlm(), batchSize: 1);

        // 提取失败不应向上抛，消息链路不受影响
        await h.Extractor.AddMessageAsync("13", "张三", "我养猫");

        Assert.Empty(await h.Claims.GetAllAsync());
        Assert.Equal(0, h.Extractor.ExtractCount);
    }

    private sealed class ThrowingLlm : IExtractionLlm
    {
        public Task<string?> ExtractClaimsAsync(string context)
            => throw new InvalidOperationException("模型不可用");
    }
}
