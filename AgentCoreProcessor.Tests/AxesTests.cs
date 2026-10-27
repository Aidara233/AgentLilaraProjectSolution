using System;
using System.Collections.Generic;
using System.Linq;
using AgentCoreProcessor.Engine.Axes;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 轴表与轴值存储的契约。
/// ⚠️ 不建会话、不碰存储、不联网——纯内存。
///
/// <para>这批测试补的是一个真实缺口：A1 落地了 <see cref="AxesCatalog"/>（334 行）与
/// <see cref="AxisStore"/>（99 行）时**一条测试都没写**，全靠"零消费者"来保证零行为变化。
/// 那在"还没有消费者"时成立，但表本身的契约（大小写、网格、NaN、默认值）是将来接线的地基，
/// 必须现在钉住。</para>
/// </summary>
public class AxesTests
{
    // ── 表的完整性 ────────────────────────────────────────────

    [Fact]
    public void EveryAxis_HasAtLeastTwoValues()
    {
        foreach (var axis in AxesCatalog.Names)
        {
            var values = AxesCatalog.ValuesOf(axis);
            Assert.NotNull(values);
            Assert.True(values!.Count >= 2, $"{axis} 只有 {values.Count} 个取值，那不是一个可选的轴");
        }
    }

    [Fact]
    public void EveryAxis_HasItsOwnChineseLabel_NotTheFallbackName()
    {
        foreach (var axis in AxesCatalog.Names)
        {
            var label = AxesCatalog.LabelOf(axis);
            // ⚠️ LabelOf 的兜底是"原样返回轴名"，所以 label == axis 说明表里漏了 Label。
            Assert.NotEqual(axis, label);
            Assert.False(string.IsNullOrWhiteSpace(label));
        }
    }

    [Fact]
    public void EveryAxis_DefaultIsOneOfItsValues()
    {
        foreach (var (axis, value) in AxesCatalog.Defaults())
        {
            var values = AxesCatalog.ValuesOf(axis);
            Assert.NotNull(values);
            Assert.Contains(value, values!);
        }
    }

    [Fact]
    public void Names_AndRenderOrder_ArePermutationsOfEachOther()
    {
        Assert.Equal(
            AxesCatalog.Names.OrderBy(n => n, StringComparer.Ordinal),
            AxesCatalog.RenderOrder);
    }

    [Fact]
    public void RenderOrder_IsOrdinalNotCulture()
    {
        // 表序是给人看的（按语义分组），RenderOrder 必须是 ordinal —— 逐机一致的字节。
        Assert.Equal(
            AxesCatalog.Names.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            AxesCatalog.RenderOrder.ToArray());
    }

    // ── TryParse：轴名敏感、取值不敏感 ──────────────────────────

    [Fact]
    public void TryParse_ValueIsCaseInsensitive_AndCanonicalised()
    {
        Assert.True(AxesCatalog.TryParse(AxesCatalog.Safety, "PROD", out var canonical));
        Assert.Equal("prod", canonical);
    }

    [Fact]
    public void TryParse_AxisNameIsCaseSensitive()
    {
        Assert.False(AxesCatalog.TryParse("Safety", "dev", out _));
    }

    [Fact]
    public void TryParse_UnknownAxisAndNull_AreRejected()
    {
        Assert.False(AxesCatalog.TryParse("nope", "x", out _));
        Assert.False(AxesCatalog.TryParse(null, "x", out _));
    }

    [Fact]
    public void TryParse_IllegalValue_IsRejected()
    {
        Assert.False(AxesCatalog.TryParse(AxesCatalog.Safety, "production", out _));
    }

    // ── 数值轴：网格与拒绝 ────────────────────────────────────

    [Fact]
    public void Temperature_DefaultIsZeroPointSeven_MatchingApiClientCfg()
    {
        // ⚠️ 不是上游的 1.0：本项目 ApiClientCfg.Temperature 的既有默认就是 0.7，
        // 写 1.0 会让"轴默认值"与"实际发出去的值"立刻分叉。
        Assert.Equal("0.7", AxesCatalog.Defaults()[AxesCatalog.Temperature]);
    }

    [Fact]
    public void Temperature_IsNumeric_AndInContextIsFalse()
    {
        Assert.True(AxesCatalog.IsNumeric(AxesCatalog.Temperature));
        Assert.False(AxesCatalog.InContext(AxesCatalog.Temperature));
    }

    [Theory]
    [InlineData("0", "0.0")]
    [InlineData("0.7", "0.7")]
    [InlineData("0.70", "0.7")]
    [InlineData("2", "2.0")]
    public void Temperature_OnGridValuesAreAcceptedAndCanonicalised(string input, string expected)
    {
        Assert.True(AxesCatalog.TryParse(AxesCatalog.Temperature, input, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData("0.75")]   // 落在网格之间：**拒绝**，不能悄悄吸附到 0.8
    [InlineData("2.1")]    // 越界
    [InlineData("-0.1")]   // 越界
    [InlineData("NaN")]    // double.TryParse("NaN") 会成功，必须显式挡
    [InlineData("Infinity")]
    [InlineData("abc")]
    public void Temperature_OffGridOutOfRangeAndNaNAreRejected(string input)
    {
        Assert.False(AxesCatalog.TryParse(AxesCatalog.Temperature, input, out _));
    }

    [Fact]
    public void NumberOrNull_OnlyForNumericAxes()
    {
        Assert.Equal(0.7, AxesCatalog.NumberOrNull(AxesCatalog.Temperature, "0.7"));
        // 枚举轴没有数值
        Assert.Null(AxesCatalog.NumberOrNull(AxesCatalog.Safety, "dev"));
        // 越界
        Assert.Null(AxesCatalog.NumberOrNull(AxesCatalog.Temperature, "9.9"));
    }

    // ── 进不进上下文 ──────────────────────────────────────────

    [Theory]
    [InlineData("initiative", true)]
    [InlineData("safety", true)]
    [InlineData("verify", true)]
    [InlineData("permission", false)]   // 未接线：进提示词会让模型自己拒工具
    [InlineData("network", false)]      // 同上
    [InlineData("effort", false)]       // 纯请求参数
    [InlineData("temperature", false)]  // 纯请求参数
    public void InContext_MatchesTheWiringState(string axis, bool expected)
    {
        Assert.Equal(expected, AxesCatalog.InContext(axis));
    }

    [Fact]
    public void InContext_UnknownOrNull_IsFalse()
    {
        // 宁可漏写，不可错写。
        Assert.False(AxesCatalog.InContext(null));
        Assert.False(AxesCatalog.InContext("nope"));
    }

    // ── 等级解释 ──────────────────────────────────────────────

    [Fact]
    public void InContextAxes_HaveLevelDescriptionsForEveryValue()
    {
        foreach (var axis in AxesCatalog.Names.Where(AxesCatalog.InContext))
        {
            foreach (var value in AxesCatalog.ValuesOf(axis)!)
            {
                var description = AxesCatalog.DescriptionOf(axis, value);
                Assert.False(string.IsNullOrWhiteSpace(description),
                    $"{axis}={value} 没有等级解释，但它进上下文（模型会看到半个约定）");
            }
        }
    }

    [Fact]
    public void DescriptionOf_UnknownLevelOrAxis_IsNull()
    {
        Assert.Null(AxesCatalog.DescriptionOf(AxesCatalog.Safety, "nope"));
        Assert.Null(AxesCatalog.DescriptionOf("nope", "dev"));
        Assert.Null(AxesCatalog.DescriptionOf(AxesCatalog.Safety, null));
    }

    // ── 默认值的新实例 ────────────────────────────────────────

    [Fact]
    public void Defaults_ReturnsFreshDictionaryEachTime()
    {
        var first = AxesCatalog.Defaults() as IDictionary<string, string>;
        var second = AxesCatalog.Defaults();

        Assert.NotSame(first, second);
        // 改一份不该影响另一份（共享可变实例会串会话）
        if (first is not null) first[AxesCatalog.Safety] = "prod";
        Assert.Equal("dev", second[AxesCatalog.Safety]);
    }

    // ── AxisStore ────────────────────────────────────────────

    [Fact]
    public void Store_StartsFromDefaults()
    {
        var store = new AxisStore();

        foreach (var (axis, value) in AxesCatalog.Defaults())
            Assert.Equal(value, store.Get(axis));
    }

    [Fact]
    public void Store_Reset_DropsUnknownAxesAndInvalidValues()
    {
        var store = new AxisStore();
        var dropped = store.Reset(new Dictionary<string, string>
        {
            ["no-such-axis"] = "x",                 // 未知轴
            [AxesCatalog.Safety] = "production",     // 非法值
            [AxesCatalog.Verify] = "STRICT",         // 合法（大小写不敏感 → 规范化）
        });

        Assert.Equal(2, dropped.Count);
        Assert.Contains(dropped, d => d.Axis == "no-such-axis" && d.Reason == "unknown-axis");
        Assert.Contains(dropped, d => d.Axis == AxesCatalog.Safety && d.Reason == "invalid-value");
        // 非法值不覆盖默认
        Assert.Equal("dev", store.Get(AxesCatalog.Safety));
        Assert.Equal("strict", store.Get(AxesCatalog.Verify));
    }

    [Fact]
    public void Store_Reset_FromNullMeansDefaults()
    {
        var store = new AxisStore();
        var dropped = store.Reset(null);

        Assert.Empty(dropped);
        Assert.Equal(AxesCatalog.Defaults()[AxesCatalog.Initiative], store.Get(AxesCatalog.Initiative));
    }

    [Fact]
    public void Store_TrySet_CanonicalisesAndRejects()
    {
        var store = new AxisStore();

        Assert.True(store.TrySet(AxesCatalog.Safety, "PROD", out var canonical));
        Assert.Equal("prod", canonical);
        Assert.Equal("prod", store.Get(AxesCatalog.Safety));

        Assert.False(store.TrySet(AxesCatalog.Safety, "production", out _));
        Assert.Equal("prod", store.Get(AxesCatalog.Safety));   // 非法写入不改变状态
    }

    [Fact]
    public void Store_Snapshot_IsACopy()
    {
        var store = new AxisStore();
        var snapshot = store.Snapshot();
        store.TrySet(AxesCatalog.Safety, "prod", out _);

        Assert.Equal("dev", snapshot[AxesCatalog.Safety]);
        Assert.Equal("prod", store.Get(AxesCatalog.Safety));
    }

    [Fact]
    public void Store_CompleteFromDefaults_AddsOnlyMissingAxes()
    {
        var store = new AxisStore();
        var added = store.CompleteFromDefaults();

        // 已经全是默认值，没有缺失
        Assert.Empty(added);
    }

    [Fact]
    public void Store_Get_UnknownAxisIsNull()
    {
        var store = new AxisStore();
        Assert.Null(store.Get("nope"));
        Assert.Null(store.Get(null));
    }
}
