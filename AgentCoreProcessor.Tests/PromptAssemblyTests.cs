using System;
using System.Collections.Generic;
using System.Linq;
using AgentCoreProcessor.Engine.Prompts;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 提示词装配的**顺序契约**与 **Stable 约束**。
/// ⚠️ 这些测试不建会话、不读写数据根、不联网——纯内存。
/// </summary>
public class PromptAssemblyTests
{
    private static PromptContribution C(
        string plugin, string key, PromptSlot slot, string text, bool? stable = null) =>
        new()
        {
            PluginId = plugin,
            Key = key,
            Slot = slot,
            Text = text,
            // 默认按槽位给合法值：System/Primary 必须稳定，Floating 必须不稳定。
            Stable = stable ?? slot != PromptSlot.Floating,
        };

    // ── Stable 约束 ────────────────────────────────────────────

    [Fact]
    public void SystemField_MustBeStable()
    {
        var bad = C("p", "k", PromptSlot.System, "x", stable: false);
        Assert.NotNull(bad.Validate());
        Assert.Contains("Stable=true", bad.Validate());
    }

    [Fact]
    public void PrimaryField_MustBeStable()
    {
        var bad = C("p", "k", PromptSlot.Primary, "x", stable: false);
        Assert.NotNull(bad.Validate());
    }

    [Fact]
    public void FloatingField_MustNotBeStable()
    {
        var bad = C("p", "k", PromptSlot.Floating, "x", stable: true);
        Assert.NotNull(bad.Validate());
        Assert.Contains("Stable=false", bad.Validate());
    }

    [Fact]
    public void LegalCombinations_PassValidation()
    {
        Assert.Null(C("p", "k", PromptSlot.System, "x").Validate());
        Assert.Null(C("p", "k", PromptSlot.Primary, "x").Validate());
        Assert.Null(C("p", "k", PromptSlot.Floating, "x").Validate());
    }

    [Fact]
    public void EmptyPluginIdOrKey_IsRejected()
    {
        Assert.NotNull(C("", "k", PromptSlot.System, "x").Validate());
        Assert.NotNull(C("p", "", PromptSlot.System, "x").Validate());
    }

    // ── 构建：校验 + 去重 + 排序 ────────────────────────────────

    [Fact]
    public void Build_RejectsDuplicateSlotPluginKey()
    {
        var snapshot = PromptContributionSnapshot.Build(
            [
                C("p", "k", PromptSlot.System, "a"),
                C("p", "k", PromptSlot.System, "b"),
            ],
            out var errors);

        Assert.Single(snapshot.System);
        Assert.Single(errors);
        Assert.Contains("重复", errors[0]);
    }

    [Fact]
    public void Build_KeepsSameKeyOnDifferentSlots()
    {
        var snapshot = PromptContributionSnapshot.Build(
            [
                C("p", "k", PromptSlot.System, "a"),
                C("p", "k", PromptSlot.Primary, "b"),
            ],
            out var errors);

        Assert.Empty(errors);
        Assert.Single(snapshot.System);
        Assert.Single(snapshot.Primary);
    }

    [Fact]
    public void Build_ReportsEveryBadFieldInsteadOfStoppingAtFirst()
    {
        var snapshot = PromptContributionSnapshot.Build(
            [
                C("p", "a", PromptSlot.System, "x", stable: false),
                C("p", "b", PromptSlot.Floating, "y", stable: true),
                C("p", "c", PromptSlot.Primary, "z", stable: false),
            ],
            out var errors);

        Assert.Empty(snapshot.System);
        Assert.Empty(snapshot.Primary);
        Assert.Empty(snapshot.Floating);
        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void Build_SortsByPluginThenKey_Ordinal()
    {
        var snapshot = PromptContributionSnapshot.Build(
            [
                C("zzz", "a", PromptSlot.Primary, "3"),
                C("aaa", "b", PromptSlot.Primary, "2"),
                C("aaa", "a", PromptSlot.Primary, "1"),
            ],
            out _);

        // ⚠️ 比 (pluginId, key) 而不是 SortKey：SortKey 里的分隔符是内部实现细节（NUL），
        // 把它写进断言等于把实现细节钉成契约。
        Assert.Equal(
            ["aaa/a", "aaa/b", "zzz/a"],
            snapshot.Primary.Select(c => $"{c.PluginId}/{c.Key}"));
    }

    // ── 顺序稳定性（本文件最重要的一条）─────────────────────────

    [Fact]
    public void Join_IsByteIdenticalAcrossTwoCalls()
    {
        var snapshot = PromptContributionSnapshot.Build(
            [
                C("b", "2", PromptSlot.Floating, "第二"),
                C("a", "1", PromptSlot.Floating, "第一"),
                C("b", "1", PromptSlot.Floating, "也第二"),
            ],
            out _);

        var first = snapshot.Text(PromptSlot.Floating);
        var second = snapshot.Text(PromptSlot.Floating);

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Join_IsByteIdenticalRegardlessOfInputOrder()
    {
        var items = new[]
        {
            C("b", "2", PromptSlot.Floating, "第二"),
            C("a", "1", PromptSlot.Floating, "第一"),
            C("b", "1", PromptSlot.Floating, "也第二"),
        };

        var forward = PromptContributionSnapshot.Build(items, out _).Text(PromptSlot.Floating);
        var reversed = PromptContributionSnapshot.Build(items.Reverse().ToArray(), out _).Text(PromptSlot.Floating);

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void EmptySlot_JoinsToNull_NotEmptyString()
    {
        var snapshot = PromptContributionSnapshot.Build([], out _);

        // ⚠️ 「没有块」与「空块」是两件事：null 才是"没有"。
        Assert.Null(snapshot.Text(PromptSlot.System));
        Assert.Null(snapshot.Text(PromptSlot.Primary));
        Assert.Null(snapshot.Text(PromptSlot.Floating));
    }

    // ── 装配：Order 分配与校验 ─────────────────────────────────

    [Fact]
    public void Assemble_AssignsContiguousOrderPerSlot()
    {
        var snapshot = PromptContributionSnapshot.Build(
            [
                C("p", "c", PromptSlot.Primary, "3"),
                C("p", "a", PromptSlot.Primary, "1"),
                C("p", "b", PromptSlot.Primary, "2"),
            ],
            out _);

        var assembly = PromptOrder.Assemble(snapshot);
        var primary = assembly.BlocksOf(PromptSlot.Primary);

        Assert.Equal([0, 1, 2], primary.Select(b => b.Order));
        Assert.Equal(["a", "b", "c"], primary.Select(b => b.Key));
        Assert.Empty(assembly.ValidateOrder());
    }

    [Fact]
    public void Assemble_KeepsSlotsInProtocolOrder()
    {
        var snapshot = PromptContributionSnapshot.Build(
            [
                C("p", "f", PromptSlot.Floating, "f"),
                C("p", "s", PromptSlot.System, "s"),
                C("p", "p", PromptSlot.Primary, "p"),
            ],
            out _);

        var assembly = PromptOrder.Assemble(snapshot);
        Assert.Equal(
            [PromptSlot.System, PromptSlot.Primary, PromptSlot.Floating],
            assembly.Slots.Select(b => b.Slot));
    }

    [Fact]
    public void Assemble_OwnerIsCoreOrPluginPrefix()
    {
        var snapshot = PromptContributionSnapshot.Build(
            [
                C("style", "detail", PromptSlot.Floating, "详细"),
                C(PromptContribution.CoreOwner, "base", PromptSlot.System, "核心"),
            ],
            out var errors);

        Assert.Empty(errors);

        var assembly = PromptOrder.Assemble(snapshot);
        Assert.Equal("plugin:style", assembly.BlocksOf(PromptSlot.Floating).Single().Owner);
        Assert.Equal("core", assembly.BlocksOf(PromptSlot.System).Single().Owner);
    }

    [Fact]
    public void ValidateOrder_RejectsDuplicateOrderWithinSlot()
    {
        var assembly = new PromptAssembly(
            [],
            [
                new PromptBlock(PromptSlot.System, "core", "a", "x", true, Order: 0),
                new PromptBlock(PromptSlot.System, "core", "b", "y", true, Order: 0),
            ]);

        var errors = assembly.ValidateOrder();
        Assert.Single(errors);
        Assert.Contains("重复", errors[0]);
    }

    [Fact]
    public void ValidateOrder_AllowsSameOrderAcrossDifferentSlots()
    {
        var assembly = new PromptAssembly(
            [],
            [
                new PromptBlock(PromptSlot.System, "core", "a", "x", true, Order: 0),
                new PromptBlock(PromptSlot.Primary, "core", "b", "y", true, Order: 0),
            ]);

        // 各槽位独立编号，所以跨槽位同号是合法的。
        Assert.Empty(assembly.ValidateOrder());
    }

    [Fact]
    public void Leading_IsSeparateFromSlots()
    {
        var leading = new[] { new PromptBlock(PromptSlot.Floating, "core", "floating", "读数", false, 0) };
        var assembly = PromptOrder.Assemble(PromptContributionSnapshot.Empty, leading);

        Assert.Single(assembly.Leading);
        Assert.Empty(assembly.Slots);
        Assert.Empty(assembly.ValidateOrder());
    }

    [Fact]
    public void TextOf_IncludesFieldLabelForProvenance()
    {
        var snapshot = PromptContributionSnapshot.Build(
            [C("style", "detail", PromptSlot.Floating, "详细")],
            out _);

        var assembly = PromptOrder.Assemble(snapshot);
        var text = assembly.TextOf(PromptSlot.Floating);

        Assert.Equal("[plugin:style/detail]\n详细", text);
    }
}
