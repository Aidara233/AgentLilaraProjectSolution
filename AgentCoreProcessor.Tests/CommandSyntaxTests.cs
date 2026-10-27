using AgentCoreProcessor.Command;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class CommandSyntaxTests
{
    [Theory]
    [InlineData(" /help  one two  ", "/", "help", "one two")]
    [InlineData("/  help", "/", "help", "")]
    [InlineData("/help\targ", "/", "help\targ", "")]
    [InlineData("/help \t arg \t", "/", "help", "arg")]
    [InlineData("hello arg", "", "hello", "arg")]
    [InlineData("!!HELP", "!!", "HELP", "")]
    public void PreservesExistingSplitAndTrimRules(string text, string prefix, string name, string args)
    {
        Assert.Equal(new ParsedCommand(name, args), CommandSyntax.Parse(text, prefix));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData(" / \t ")]
    [InlineData("hello")]
    public void NonCommandsReturnNull(string? text) => Assert.Null(CommandSyntax.Parse(text, "/"));

    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    public void PrefixComparisonRetainsCurrentCultureSemantics(string culture)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new(culture);
            foreach (var prefix in new[] { "i", "I", "\u00e9", "" })
            foreach (var text in new[] { "item arg", "Item arg", "e\u0301help arg" })
            {
                var expected = text.StartsWith(prefix);
                Assert.Equal(expected, CommandSyntax.Parse(text, prefix) is not null);
            }
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void StepPromptKeepsExactOptionsAndNewline()
    {
        Assert.Equal("prompt", CommandSyntax.FormatStepPrompt(new() { Key = "x", Prompt = "prompt" }));
        Assert.Equal("prompt", CommandSyntax.FormatStepPrompt(new() { Key = "x", Prompt = "prompt", Options = new() }));
        Assert.Equal("prompt\n" + Environment.NewLine + "可选: A / b",
            CommandSyntax.FormatStepPrompt(new() { Key = "x", Prompt = "prompt\n", Options = new() { "A", "b" } }));
        Assert.Equal("失败: error", CommandResult.Fail("error").Response);
        Assert.True(CommandResult.Ok("ok").Success);
    }

    [Fact]
    public void ConfigurationDefaultsAndNullJsonRemainCompatible()
    {
        var root = Path.Combine(Path.GetTempPath(), "command-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "config.json");
            Assert.Equal("/", CommandConfig.Load(path).Prefix);
            File.WriteAllText(path, "null");
            Assert.Equal("/", CommandConfig.Load(path).Prefix);
            File.WriteAllText(path, "{\"Prefix\":\"\"}");
            Assert.Equal("", CommandConfig.Load(path).Prefix);
        }
        finally { Directory.Delete(root, true); }
    }
}
