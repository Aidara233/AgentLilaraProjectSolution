using System;
using System.Collections.Generic;
using System.Linq;
using AgentCoreProcessor.Engine.Axes;
using AgentCoreProcessor.Engine.Context;
using AgentCoreProcessor.Engine.Prompts;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 轴 → 提示词浮动槽的投影，以及装配器的纯函数契约。
/// ⚠️ 不建会话、不读写数据根、不联网。
/// </summary>
public class ContextAssemblerTests
{
    private static AxisSnapshot Snapshot(params (string Axis, string Value)[] overrides)
    {
        var values = new Dictionary<string, string>(AxesCatalog.Defaults(), StringComparer.Ordinal);
        foreach (var (axis, value) in overrides) values[axis] = value;
        return new AxisSnapshot(values, DateTimeOffset.UnixEpoch);
    }

    // ── 轴表 ──────────────────────────────────────────────────

    [Fact]
    public void AxisTable_ContainsOnlyInContextAxes()
    {
        var table = ContextAssembler.AxisTable(Snapshot());

        Assert.NotNull(table);
        Assert.Contains("主动性", table);
        Assert.Contains("安全", table);
        Assert.Contains("验证强度", table);

        // ⚠️ 未接线的轴绝不能出现在提示词里：模型读到"权限 off"会自己把工具调用拒掉。
        Assert.DoesNotContain("权限", table);
        Assert.DoesNotContain("联网", table);
        Assert.DoesNotContain("温度", table);
        Assert.DoesNotContain("思考强度", table);
    }

    [Fact]
    public void AxisTable_UsesRenderOrderNotTableOrder()
    {
        var table = ContextAssembler.AxisTable(Snapshot());

        // RenderOrder 是 ordinal 序（initiative/safety/verify 恰好也是字母序），
        // 而表序是 initiative/permission/safety/verify/network/effort/temperature。
        // 这里只断言"进上下文的三个轴"的相对顺序与 RenderOrder 一致。
        var expected = AxesCatalog.RenderOrder
            .Where(AxesCatalog.InContext)
            .Select(a => $"{AxesCatalog.LabelOf(a)} ")
            .ToArray();

        Assert.NotNull(table);
        var positions = expected.Select(e => table.IndexOf(e, StringComparison.Ordinal)).ToArray();
        Assert.DoesNotContain(-1, positions);
        for (var i = 1; i < positions.Length; i++)
            Assert.True(positions[i - 1] < positions[i], "轴表未按 RenderOrder 排列");
    }

    [Fact]
    public void AxisTable_ReturnsNullWhenNoAxisInContext()
    {
        // 空值快照：所有轴都"取不到"，于是这一块整个不产出（而不是产出一行空的）。
        var empty = new AxisSnapshot(new Dictionary<string, string>(), DateTimeOffset.UnixEpoch);
        Assert.Null(ContextAssembler.AxisTable(empty));
    }

    [Fact]
    public void AxisTable_IsNullForNullSnapshot()
    {
        Assert.Null(ContextAssembler.AxisTable(null));
    }

    [Fact]
    public void AxisTable_ReflectsCurrentValue()
    {
        var table = ContextAssembler.AxisTable(Snapshot((AxesCatalog.Safety, "prod")));

        Assert.NotNull(table);
        Assert.Contains("安全 prod", table);
    }

    // ── 轴贡献 ────────────────────────────────────────────────

    [Fact]
    public void AxisContributions_ProduceTablePlusOnePerInContextAxis()
    {
        var contributions = ContextAssembler.AxisContributions(Snapshot());
        var inContext = AxesCatalog.RenderOrder.Count(AxesCatalog.InContext);

        // 一条表 + 每个进上下文的轴一条等级解释
        Assert.Equal(1 + inContext, contributions.Count);
        Assert.All(contributions, c => Assert.Equal(PromptSlot.Floating, c.Slot));
        Assert.All(contributions, c => Assert.False(c.Stable));
        Assert.All(contributions, c => Assert.Equal(PromptContribution.CoreOwner, c.PluginId));
    }

    [Fact]
    public void AxisContributions_OnlyDescribeTheSelectedLevel()
    {
        var contributions = ContextAssembler.AxisContributions(Snapshot((AxesCatalog.Safety, "prod")));
        var safety = contributions.Single(c => c.Key == "axis-level.safety");

        Assert.Contains("prod", safety.Text);
        // ⚠️ 不把其他档一起塞进去：那是每步都付全价的槽位。
        Assert.DoesNotContain("dev", safety.Text);
        Assert.DoesNotContain("staging", safety.Text);
    }

    [Fact]
    public void AxisContributions_UseReservedKeyPrefix()
    {
        var contributions = ContextAssembler.AxisContributions(Snapshot());

        Assert.Contains(contributions, c => c.Key == "axis-table");
        Assert.All(
            contributions.Where(c => c.Key.StartsWith("axis-level", StringComparison.Ordinal)),
            c => Assert.StartsWith("axis-level.", c.Key, StringComparison.Ordinal));
    }

    [Fact]
    public void AxisContributions_AllowNoDuplicateKeys()
    {
        var contributions = ContextAssembler.AxisContributions(Snapshot());

        var keys = contributions.Select(c => c.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    // ── 装配 ──────────────────────────────────────────────────

    [Fact]
    public void Assemble_WithAxes_ProducesFloatingOnlyWhenNoOtherSources()
    {
        var assembly = ContextAssembler.AssembleWithAxes(Snapshot(), null, null, out var errors);

        Assert.Empty(errors);
        Assert.Empty(assembly.BlocksOf(PromptSlot.System));
        Assert.Empty(assembly.BlocksOf(PromptSlot.Primary));
        Assert.NotEmpty(assembly.BlocksOf(PromptSlot.Floating));
        Assert.Empty(assembly.ValidateOrder());
    }

    [Fact]
    public void Assemble_IsDeterministic()
    {
        var axes = Snapshot();
        var extra = new[]
        {
            ContextAssembler.CoreSystem("template", "会话模板"),
            ContextAssembler.CorePrimary("project", "项目说明"),
        };

        var first = ContextAssembler.AssembleWithAxes(axes, extra, null, out _);
        var second = ContextAssembler.AssembleWithAxes(axes, extra, null, out _);

        foreach (var slot in new[] { PromptSlot.System, PromptSlot.Primary, PromptSlot.Floating })
            Assert.Equal(first.TextOf(slot), second.TextOf(slot));
    }

    [Fact]
    public void Assemble_KeepsLeadingOutOfSlots()
    {
        var leading = new[] { new PromptBlock(PromptSlot.Floating, "core", "floating", "读数", false, 0) };
        var assembly = ContextAssembler.AssembleWithAxes(Snapshot(), null, leading, out _);

        Assert.Single(assembly.Leading);
        Assert.DoesNotContain(assembly.Slots, b => b.Key == "floating");
    }

    [Fact]
    public void Assemble_RejectsUnstableSystemFieldButKeepsValidOnes()
    {
        var bad = new PromptContribution
        {
            PluginId = "p", Key = "k", Slot = PromptSlot.System, Text = "x", Stable = false,
        };

        var assembly = ContextAssembler.Assemble([ContextAssembler.CoreSystem("template", "ok"), bad], null, out var errors);

        Assert.Single(errors);
        Assert.Single(assembly.BlocksOf(PromptSlot.System));
        Assert.Contains("ok", assembly.TextOf(PromptSlot.System));
    }

    [Fact]
    public void CoreFactories_ProduceValidContributions()
    {
        Assert.Null(ContextAssembler.CoreSystem("a", "x").Validate());
        Assert.Null(ContextAssembler.CorePrimary("b", "x").Validate());
        Assert.Null(ContextAssembler.CoreFloating("c", "x").Validate());
    }

    [Fact]
    public void Describe_ReportsSlotCounts()
    {
        var assembly = ContextAssembler.AssembleWithAxes(Snapshot(), null, null, out _);
        var text = ContextAssembler.Describe(assembly);

        Assert.StartsWith("leading=0", text);
        Assert.Contains("System=0", text);
        Assert.Contains("Floating=", text);
    }
}
