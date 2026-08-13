// AgentCoreProcessor.Tests/ForwardMessageRendererTests.cs
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgentCoreProcessor.Tests;

public class ForwardMessageRendererTests
{
    /// <summary>用内存字典模拟 get_forward_msg 的 data.messages 返回。</summary>
    private static Task<ForwardMessageTree?> BuildWith(Dictionary<string, JArray> store, string id, int maxDepth = 3)
        => ForwardMessageTree.BuildAsync(id, fwdId => Task.FromResult(store.GetValueOrDefault(fwdId)), maxDepth);

    // ── node 格式（官方文档示例）──

    [Fact]
    public async Task NodeFormat_ParsesSendersAndText()
    {
        var store = new Dictionary<string, JArray>
        {
            ["A"] = JArray.Parse("""
                [
                  {"type":"node","data":{"user_id":123456,"nickname":"User1",
                    "message":[{"type":"text","data":{"text":"Message 1"}}]}},
                  {"type":"node","data":{"user_id":789012,"nickname":"User2",
                    "message":[{"type":"text","data":{"text":"Message 2"}}]}}
                ]
                """)
        };

        var tree = await BuildWith(store, "A");
        Assert.NotNull(tree);

        Assert.Equal(2, tree!.DirectCount);
        Assert.Equal(0, tree.NestedCount);
        Assert.Equal(2, tree.TotalCount);
        Assert.Equal(2, tree.ParticipantCount);
        Assert.Equal("User1", tree.Roots[0].SenderName);
        Assert.Equal("Message 1", tree.Roots[0].Text);

        var summary = tree.RenderSummary();
        Assert.Contains("共2条", summary);
        Assert.Contains($"约{tree.CharCount}字", summary);

        var (fullText, urls) = tree.RenderFullText();
        Assert.Contains("[1] User1: Message 1", fullText);
        Assert.Contains("[2] User2: Message 2", fullText);
        Assert.Empty(urls);
    }

    // ── OB11 完整对象格式（NapCat Issue #1174 真实报文结构）──

    [Fact]
    public async Task Ob11Format_CardNameTakesPriority_ParsesImageUrls()
    {
        var store = new Dictionary<string, JArray>
        {
            ["A"] = JArray.Parse("""
                [
                  {"user_id":3051561876,
                   "sender":{"user_id":3051561876,"nickname":"昵称","card":"群名片"},
                   "message":[
                     {"type":"text","data":{"text":"看图"}},
                     {"type":"image","data":{"url":"http://example.com/a.jpg"}}
                   ]}
                ]
                """)
        };

        var tree = await BuildWith(store, "A");
        Assert.NotNull(tree);

        Assert.Equal(1, tree!.DirectCount);
        Assert.Equal(1, tree.ImageCount);
        Assert.Equal("群名片", tree.Roots[0].SenderName); // card 优先 nickname
        Assert.Equal("看图", tree.Roots[0].Text);
        Assert.Equal("http://example.com/a.jpg", tree.Roots[0].ImageUrls[0]);

        var (fullText, urls) = tree.RenderFullText();
        Assert.Contains("[1] 群名片: 看图 [图片1]", fullText);
        Assert.Single(urls);
        Assert.Equal("http://example.com/a.jpg", urls[0]);
    }

    // ── 嵌套递归统计（Windows 文件夹式）──

    [Fact]
    public async Task Nested_RecursivelyCountsDirectNestedAndParticipants()
    {
        var store = new Dictionary<string, JArray>
        {
            ["A"] = JArray.Parse("""
                [
                  {"type":"node","data":{"user_id":1,"nickname":"甲",
                    "message":[{"type":"text","data":{"text":"在聊"}}]}},
                  {"type":"node","data":{"user_id":2,"nickname":"乙",
                    "message":[{"type":"forward","data":{"id":"B"}}]}},
                  {"type":"node","data":{"user_id":1,"nickname":"甲",
                    "message":[{"type":"text","data":{"text":"继续"}}]}}
                ]
                """),
            ["B"] = JArray.Parse("""
                [
                  {"type":"node","data":{"user_id":3,"nickname":"丙",
                    "message":[{"type":"text","data":{"text":"内层1"}}]}},
                  {"type":"node","data":{"user_id":4,"nickname":"丁",
                    "message":[{"type":"text","data":{"text":"内层2"}}]}},
                  {"type":"node","data":{"user_id":3,"nickname":"丙",
                    "message":[{"type":"text","data":{"text":"内层3"}}]}}
                ]
                """)
        };

        var tree = await BuildWith(store, "A");
        Assert.NotNull(tree);

        Assert.Equal(3, tree!.DirectCount);
        Assert.Equal(3, tree.NestedCount);
        Assert.Equal(6, tree.TotalCount);
        Assert.Equal(4, tree.ParticipantCount); // 甲乙丙丁
        Assert.Equal(1, tree.NestedForwardCount);
        Assert.Equal(2, tree.MaxDepth);
        Assert.False(tree.Truncated);
        Assert.Equal("B", tree.Roots[1].NestedForwardId);

        var summary = tree.RenderSummary();
        Assert.Contains("直接3条 + 嵌套3条 = 共6条", summary);
        Assert.Contains("4人", summary);
        Assert.Contains("嵌套1个(最深2层)", summary);

        var (fullText, _) = tree.RenderFullText();
        Assert.Contains("[2] 乙: [合并转发 id=B]", fullText);
        Assert.Contains("  [1] 丙: 内层1", fullText); // 嵌套缩进每层 2 空格
        Assert.Contains("  [3] 丙: 内层3", fullText);
    }

    // ── 深度超限截断 ──

    [Fact]
    public async Task Nested_ExceedsMaxDepth_MarksTruncated()
    {
        var store = new Dictionary<string, JArray>
        {
            ["A"] = JArray.Parse("""
                [
                  {"type":"node","data":{"user_id":1,"nickname":"甲",
                    "message":[{"type":"forward","data":{"id":"B"}}]}}
                ]
                """),
            ["B"] = JArray.Parse("""
                [
                  {"type":"node","data":{"user_id":2,"nickname":"乙",
                    "message":[{"type":"text","data":{"text":"深一层"}}]}}
                ]
                """)
        };

        // maxDepth=1：顶层已是第 1 层，A 内的 forward 直接截断
        var tree = await BuildWith(store, "A", maxDepth: 1);
        Assert.NotNull(tree);
        Assert.True(tree!.Truncated);
        Assert.Equal(1, tree.DirectCount);
        Assert.Equal(0, tree.NestedCount);
        Assert.Contains("更深层未展开", tree.RenderSummary());
        Assert.Contains("嵌套过深未展开", tree.Roots[0].Text);

        // maxDepth=3：A→B 正常展开
        var deep = await BuildWith(store, "A", maxDepth: 3);
        Assert.NotNull(deep);
        Assert.False(deep!.Truncated);
        Assert.Equal(1, deep.NestedCount);
    }

    // ── raw_message 退化：无 message 数组时用原文 ──

    [Fact]
    public async Task MissingMessageArray_FallsBackToRawMessage()
    {
        var store = new Dictionary<string, JArray>
        {
            ["A"] = JArray.Parse("""
                [
                  {"user_id":1,"sender":{"user_id":1,"nickname":"甲"},
                   "raw_message":"原始CQ码文本"}
                ]
                """)
        };

        var tree = await BuildWith(store, "A");
        Assert.NotNull(tree);
        Assert.Equal("原始CQ码文本", tree!.Roots[0].Text);
    }

    // ── 顶层拉取失败返回 null ──

    [Fact]
    public async Task TopLevelFetchFailure_ReturnsNull()
    {
        var store = new Dictionary<string, JArray>();
        var tree = await BuildWith(store, "missing");
        Assert.Null(tree);
    }

    // ── at / 多种段混合 ──

    [Fact]
    public async Task MixedSegments_AtAndTextConcatenate()
    {
        var store = new Dictionary<string, JArray>
        {
            ["A"] = JArray.Parse("""
                [
                  {"type":"node","data":{"user_id":1,"nickname":"甲",
                    "message":[
                      {"type":"at","data":{"qq":"2","name":"乙"}},
                      {"type":"text","data":{"text":" 你好"}}
                    ]}}
                ]
                """)
        };

        var tree = await BuildWith(store, "A");
        Assert.NotNull(tree);
        Assert.Equal("@乙  你好", tree!.Roots[0].Text);
    }
}
