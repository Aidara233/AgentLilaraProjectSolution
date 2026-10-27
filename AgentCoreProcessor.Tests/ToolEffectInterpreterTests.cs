using System;
using System.Linq;
using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 工具结果 → 控制流 的**纯函数**契约。
/// ⚠️ 不建会话、不碰存储、不调模型——这正是把它抽出来的目的。
/// </summary>
public class ToolEffectInterpreterTests
{
    private static ToolEffectContext Ctx(
        string type = "channel",
        int children = 0,
        bool inFlight = false,
        bool canExit = true,
        int forceAttempts = 0) =>
        new(type, children, inFlight, canExit, forceAttempts);

    private static ToolCall Call(string tool, params string[] inputs) =>
        new() { Tool = tool, Inputs = inputs.ToList() };

    private static ToolResult Ok(string? data = null) =>
        new() { Status = "success", Data = data };

    private static ToolResult Fail() => new() { Status = "failed", Error = "boom" };

    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // ── 失败工具不触发任何效果 ─────────────────────────────────

    [Theory]
    [InlineData("end_session")]
    [InlineData("wait")]
    [InlineData("escalate")]
    [InlineData("connect_channel")]
    [InlineData("compact")]
    public void FailedTool_ProducesNoEffect(string tool)
    {
        var effect = ToolEffectInterpreter.Interpret(Call(tool), Fail(), Ctx(type: "express"), Now);

        Assert.False(effect.EndBurst);
        Assert.False(effect.EndSession);
        Assert.Empty(effect.Actions);
    }

    // ── end_session ───────────────────────────────────────────

    [Fact]
    public void EndSession_WithActiveChildren_WarnsAndKeepsSession()
    {
        var effect = ToolEffectInterpreter.Interpret(Call("end_session"), Ok(), Ctx(children: 2), Now);

        Assert.False(effect.EndSession);
        var inject = Assert.IsType<ToolEffectAction.InjectContext>(Assert.Single(effect.Actions));
        Assert.Contains("2 个活跃子 agent", inject.Text);
    }

    [Fact]
    public void EndSession_WithInFlightResources_FirstAttempt_WarnsAndCounts()
    {
        var effect = ToolEffectInterpreter.Interpret(
            Call("end_session"), Ok(), Ctx(inFlight: true, forceAttempts: 0), Now);

        Assert.False(effect.EndSession);
        // ⚠️ 顺序是契约：先注入警告，再计数（Applier 按序执行）。
        Assert.IsType<ToolEffectAction.InjectContext>(effect.Actions[0]);
        Assert.IsType<ToolEffectAction.CountForceExitAttempt>(effect.Actions[1]);
    }

    [Fact]
    public void EndSession_WithInFlightResources_SecondAttempt_ForcesExit()
    {
        var effect = ToolEffectInterpreter.Interpret(
            Call("end_session"), Ok("完成了"), Ctx(inFlight: true, forceAttempts: 1), Now);

        Assert.True(effect.EndSession);
        Assert.IsType<ToolEffectAction.Note>(effect.Actions[0]);
        var send = Assert.IsType<ToolEffectAction.SendMessage>(effect.Actions[1]);
        Assert.Equal("完成了", send.Text);
    }

    [Fact]
    public void EndSession_BlockedByComponentGate_TreatedAsResources()
    {
        // canExit=false 与 inFlight 同一条分支（原实现 `!await QueryCanExitAsync()` 在 || 后半）。
        var effect = ToolEffectInterpreter.Interpret(
            Call("end_session"), Ok(), Ctx(canExit: false, forceAttempts: 0), Now);

        Assert.False(effect.EndSession);
        Assert.Contains(effect.Actions, a => a is ToolEffectAction.CountForceExitAttempt);
    }

    [Fact]
    public void EndSession_Clean_SpeaksAndEnds()
    {
        var effect = ToolEffectInterpreter.Interpret(Call("end_session"), Ok(), Ctx(), Now);

        Assert.True(effect.EndSession);
        var send = Assert.IsType<ToolEffectAction.SendMessage>(Assert.Single(effect.Actions));
        Assert.Equal("工作完成。", send.Text);
    }

    [Fact]
    public void EndSession_UsesToolDataAsFinalMessage()
    {
        var effect = ToolEffectInterpreter.Interpret(Call("end_session"), Ok("全部办完"), Ctx(), Now);

        var send = Assert.IsType<ToolEffectAction.SendMessage>(Assert.Single(effect.Actions));
        Assert.Equal("全部办完", send.Text);
    }

    // ── compact ───────────────────────────────────────────────

    [Fact]
    public void Compact_EndsSessionWithoutActions()
    {
        var effect = ToolEffectInterpreter.Interpret(Call("compact"), Ok(), Ctx(), Now);

        Assert.True(effect.EndSession);
        Assert.False(effect.EndBurst);
        Assert.Empty(effect.Actions);
    }

    // ── wait ──────────────────────────────────────────────────

    [Fact]
    public void Wait_SetsDeadlineAndEndsBurst()
    {
        var effect = ToolEffectInterpreter.Interpret(Call("wait"), Ok(), Ctx(), Now);

        Assert.True(effect.EndBurst);
        Assert.False(effect.EndSession);
        var set = Assert.IsType<ToolEffectAction.SetWaitUntil>(Assert.Single(effect.Actions));
        Assert.Equal(Now.AddMinutes(5), set.Until);
    }

    [Fact]
    public void Wait_UsesGivenMinutes()
    {
        var effect = ToolEffectInterpreter.Interpret(Call("wait", "", "30"), Ok(), Ctx(), Now);

        var set = Assert.IsType<ToolEffectAction.SetWaitUntil>(Assert.Single(effect.Actions));
        Assert.Equal(Now.AddMinutes(30), set.Until);
    }

    [Fact]
    public void Wait_WithJournal_AppendsJournalAction()
    {
        var effect = ToolEffectInterpreter.Interpret(Call("wait", "", "5", "记一笔"), Ok(), Ctx(), Now);

        Assert.Equal(2, effect.Actions.Count);
        Assert.IsType<ToolEffectAction.SetWaitUntil>(effect.Actions[0]);
        var journal = Assert.IsType<ToolEffectAction.WriteJournal>(effect.Actions[1]);
        Assert.Equal("记一笔", journal.Raw);
    }

    [Fact]
    public void Wait_BlankJournal_DoesNotAppendAction()
    {
        var effect = ToolEffectInterpreter.Interpret(Call("wait", "", "5", "   "), Ok(), Ctx(), Now);
        Assert.Single(effect.Actions);
    }

    // ── escalate ──────────────────────────────────────────────

    [Fact]
    public void Escalate_OnExpressSession_EmitsEscalateAndEnds()
    {
        var effect = ToolEffectInterpreter.Interpret(
            Call("escalate"), Ok("需要更多能力"), Ctx(type: "express"), Now);

        Assert.True(effect.EndSession);
        var escalate = Assert.IsType<ToolEffectAction.Escalate>(Assert.Single(effect.Actions));
        Assert.Equal("需要更多能力", escalate.Reason);
    }

    [Fact]
    public void Escalate_OnNonExpressSession_IsNoOp()
    {
        var effect = ToolEffectInterpreter.Interpret(Call("escalate"), Ok("x"), Ctx(type: "channel"), Now);

        Assert.False(effect.EndSession);
        Assert.Empty(effect.Actions);
    }

    // ── 频道连接 ──────────────────────────────────────────────

    [Fact]
    public void ConnectChannel_FullAndListenDiffer()
    {
        var full = ToolEffectInterpreter.Interpret(Call("connect_channel"), Ok("42"), Ctx(), Now);
        var listen = ToolEffectInterpreter.Interpret(Call("listen_channel"), Ok("42"), Ctx(), Now);

        var a = Assert.IsType<ToolEffectAction.ConnectChannel>(Assert.Single(full.Actions));
        var b = Assert.IsType<ToolEffectAction.ConnectChannel>(Assert.Single(listen.Actions));

        Assert.Equal(42, a.ChannelId);
        Assert.Equal(ConnectionMode.Full, a.Mode);
        Assert.Equal(42, b.ChannelId);
        Assert.Equal(ConnectionMode.Listen, b.Mode);
    }

    [Fact]
    public void ConnectChannel_NonNumericData_IsNoOp()
    {
        var effect = ToolEffectInterpreter.Interpret(Call("connect_channel"), Ok("abc"), Ctx(), Now);
        Assert.Empty(effect.Actions);
    }

    [Fact]
    public void DisconnectChannel_EmitsDisconnect()
    {
        var effect = ToolEffectInterpreter.Interpret(Call("disconnect_channel"), Ok(), Ctx(), Now);

        Assert.False(effect.EndBurst);
        Assert.IsType<ToolEffectAction.DisconnectChannel>(Assert.Single(effect.Actions));
    }

    // ── 无关工具 ──────────────────────────────────────────────

    [Fact]
    public void UnrelatedTool_IsNoOp()
    {
        var effect = ToolEffectInterpreter.Interpret(Call("read_file"), Ok("..."), Ctx(), Now);

        Assert.False(effect.EndBurst);
        Assert.False(effect.EndSession);
        Assert.Empty(effect.Actions);
    }
}
