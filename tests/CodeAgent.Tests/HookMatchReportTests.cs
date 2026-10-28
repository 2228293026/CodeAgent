using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>/hooks check：回答「我配的 matcher 到底会不会命中这个工具」。
/// 钩子配错 matcher 时完全不执行、界面上毫无反应，所以这里必须给出明确结论。</summary>
public class HookMatchReportTests
{
    private static HookDefinition Hook(HookEvent ev, string matcher, string command) =>
        new(ev, matcher, command, 30);

    [Fact]
    public void ExactNameMatchesAndWildcardMatches()
    {
        var hooks = new[]
        {
            Hook(HookEvent.PreToolUse, "write_file", "guard.sh"),
            Hook(HookEvent.PostToolUse, "*", "fmt.sh"),
        };

        var lines = HookMatchReport.Format(hooks, "write_file", 120);

        Assert.Contains(lines, l => l.Contains("命中 2 / 2"));
        Assert.Contains(lines, l => l.Contains("guard.sh"));
        Assert.Contains(lines, l => l.Contains("fmt.sh"));
    }

    [Fact]
    public void PrefixIsNotAMatchAndTheHintSaysWhy()
    {
        var hooks = new[] { Hook(HookEvent.PreToolUse, "write", "guard.sh") };

        var lines = HookMatchReport.Format(hooks, "write_file", 120);

        Assert.Contains(lines, l => l.Contains("命中 0 / 1"));
        Assert.Contains(lines, l => l.Contains("前缀") && l.Contains("write"));
    }

    [Fact]
    public void NoHooksConfiguredIsSilentAboutMatchers()
    {
        var lines = HookMatchReport.Format([], "write_file", 120);

        Assert.Contains(lines, l => l.Contains("命中 0 / 0"));
        Assert.DoesNotContain(lines, l => l.Contains("前缀"));
    }
}
