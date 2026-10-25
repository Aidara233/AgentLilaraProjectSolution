using System.Security.Cryptography;
using AgentLilara.KernelBridge;
using AgentLilara.SessionBrain;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 线上请求体比较：Legacy **实际发出去的 JSON** vs 内核会构造的请求。
/// ⚠️ 不联网——Legacy 一侧的 body 是测试里手写的、与 DTO 同源的 JSON。
/// </summary>
public class WireComparerTests
{
    [Fact]
    public void Compare_NoWireBody_IsANoteNotADifference()
    {
        var request = Request(wireBody: null);

        var result = WireComparer.Compare(request, KernelRequestBuilder.Build(request).Request);

        Assert.Empty(result.Differences);
        Assert.Contains(result.Notes, n => n.Contains("没有捕获到"));
    }

    [Fact]
    public void Compare_EquivalentOpenAiBodies_HaveNoDifferences()
    {
        var request = Request(
            wireBody: OpenAiBody(
                messages: """[{"role":"system","content":"系统提示"},{"role":"user","content":"你好"}]""",
                model: "claude-opus-5",
                temperature: 0.7,
                maxTokens: 8192,
                tools: """[{"type":"function","function":{"name":"read","description":"读文件","parameters":{"type":"object"}}}]"""),
            tools: [new SessionBrainTool("read", "读文件", """{"type":"object"}""")]);

        var result = WireComparer.Compare(request, KernelRequestBuilder.Build(request).Request);

        Assert.Empty(result.Differences);
    }

    [Fact]
    public void Compare_ExtraLegacyMessage_IsReported()
    {
        var request = Request(wireBody: OpenAiBody(
            messages: """[{"role":"system","content":"系统提示"},{"role":"user","content":"你好"},{"role":"user","content":"多出来的一条"}]"""));

        var result = WireComparer.Compare(request, KernelRequestBuilder.Build(request).Request);

        Assert.Contains(result.Differences, d => d.StartsWith("wire.messages", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_ModelMismatch_IsReported()
    {
        var request = Request(wireBody: OpenAiBody(
            messages: """[{"role":"system","content":"系统提示"}]""",
            model: "another-model"));

        var result = WireComparer.Compare(request, KernelRequestBuilder.Build(request).Request);

        Assert.Contains(result.Differences, d => d.Contains("wire.model"));
    }

    // ⚠️ 图片在两种表示里都要落到同一个哈希：Legacy 是 data URL，内核是 ImageBlock(sha256)。
    [Fact]
    public void Compare_DataUrlImage_MatchesKernelImageHash()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var base64 = Convert.ToBase64String(bytes);
        var wireBody = """
            {
              "model": "claude-opus-5",
              "temperature": 0.7,
              "max_tokens": 8192,
              "stream": false,
              "messages": [
                {"role":"system","content":"系统提示"},
                {"role":"user","content":[
                  {"type":"image_url","image_url":{"url":"data:image/png;base64,BASE64_PLACEHOLDER"}}
                ]}
              ],
              "tools": []
            }
            """.Replace("BASE64_PLACEHOLDER", base64, StringComparison.Ordinal);

        var request = Request(
            messages:
            [
                new SessionBrainMessage("user",
                    [new SessionBrainImage(null, base64, "image/png")]),
            ],
            wireBody: wireBody);

        var result = WireComparer.Compare(request, KernelRequestBuilder.Build(request).Request);

        // 内核那条消息没有文本块，Legacy 的 body 里多了一个空 text 部件——这条差异是**预期**的，
        // 但图片哈希本身必须一致，不能出现「image 项对不上」。
        Assert.DoesNotContain(result.Differences, d => d.Contains("/image:", StringComparison.Ordinal));
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            WireComparer.ImageKeyFromDataUrl($"data:image/png;base64,{base64}"));
    }

    [Fact]
    public void Compare_StreamingFlag_IsOnlyANote()
    {
        var request = Request(wireBody: OpenAiBody(
            messages: """[{"role":"system","content":"系统提示"},{"role":"user","content":"你好"}]""",
            stream: true));

        var result = WireComparer.Compare(request, KernelRequestBuilder.Build(request).Request);

        Assert.Empty(result.Differences);
        Assert.Contains(result.Notes, n => n.Contains("流式"));
    }

    [Fact]
    public void Compare_ClaudeBody_ReadsToolsAndToolResults()
    {
        var request = Request(provider: "claude", wireBody: """
            {
              "model": "claude-opus-5",
              "max_tokens": 8192,
              "temperature": 0.7,
              "system": "系统提示",
              "messages": [
                {"role":"user","content":[{"type":"text","text":"你好"}]},
                {"role":"assistant","content":[{"type":"tool_use","id":"c1","name":"read","input":{"path":"a.txt"}}]},
                {"role":"user","content":[{"type":"tool_result","tool_use_id":"c1","content":"内容"}]}
              ],
              "tools": [{"name":"read","description":"读文件","input_schema":{"type":"object"}}]
            }
            """);

        var build = KernelRequestBuilder.Build(request);
        var result = WireComparer.Compare(request, build.Request);

        // DTO 里没有工具/工具调用，Legacy 的 body 里有 ⇒ 必须报出来，而不是解析失败。
        Assert.Contains(result.Differences, d => d.Contains("wire.messages"));
        Assert.Contains(result.Differences, d => d.Contains("wire.tools"));
    }

    private static string OpenAiBody(
        string messages,
        string model = "claude-opus-5",
        double temperature = 0.7,
        int maxTokens = 8192,
        string tools = "[]",
        bool stream = false) =>
        $$"""
        {
          "model": "{{model}}",
          "temperature": {{temperature}},
          "max_tokens": {{maxTokens}},
          "stream": {{(stream ? "true" : "false")}},
          "messages": {{messages}},
          "tools": {{tools}}
        }
        """;

    private static SessionBrainRequest Request(
        IReadOnlyList<SessionBrainMessage>? messages = null,
        string? wireBody = null,
        IReadOnlyList<SessionBrainTool>? tools = null,
        string provider = "openai") => new()
        {
            AlpSessionId = "s1",
            EngineType = "channel",
            WorkingDirectory = Path.GetTempPath(),
            SystemPrompt = "系统提示",
            Messages = messages ?? [new SessionBrainMessage("user", [new SessionBrainText("你好")])],
            Tools = tools ?? [],
            Provider = new SessionBrainProvider
            {
                Provider = provider,
                Endpoint = "https://example.invalid/v1/chat/completions",
                ApiKey = "k",
                Model = "claude-opus-5",
                Temperature = 0.7,
                MaxTokens = 8192,
            },
            WireBody = wireBody,
        };
}
