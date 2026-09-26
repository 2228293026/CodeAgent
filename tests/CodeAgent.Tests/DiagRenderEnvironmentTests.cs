using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// /diag 里的「渲染环境」自述。
///
/// 回归点：<c>CODEAGENT_THEME</c> / <c>CODEAGENT_ASCII</c> / <c>NO_COLOR</c> 是
/// 用户**唯一**能直接改的渲染开关，但 /diag 只报终端的客观属性，一个都没报。
/// 于是"我明明设了 contrast，怎么没生效"只能靠猜——而最常见的原因是
/// 值拼错了（<c>high-contrast</c> 之类不认识的写法会静默回落到 Dark）。
///
/// 现在把**原始值**和**判定结果**并排报出来，并把"颜色为什么是关的"一起给出。
/// </summary>
public class DiagRenderEnvironmentTests
{
    private static Func<string, string?> Env(params (string Key, string? Value)[] values) =>
        name => values.FirstOrDefault(v => v.Key == name).Value;

    private static Dictionary<string, string> Rows(Func<string, string?> env, bool redirected = false) =>
        SafeColor.DescribeEnvironment(env, redirected).ToDictionary(r => r.Key.Trim(), r => r.Value);

    [Fact]
    public void ReportsEveryKnobTheUserCanTurn()
    {
        var rows = Rows(Env(("CODEAGENT_THEME", "contrast"), ("CODEAGENT_ASCII", "1"), ("NO_COLOR", null)));
        foreach (var key in new[] { "Colors", "CODEAGENT_THEME", "Theme", "CODEAGENT_ASCII", "AsciiGlyphs" })
            Assert.True(rows.ContainsKey(key), $"缺少 {key}");
    }

    [Fact]
    public void ShowsTheRawValueSoAMisspellingIsSelfEvident()
    {
        // 用户写了 high-contrast（不认识的写法），结果静默回落 Dark。
        // 只报 Theme=Dark 用户无从判断，只报原值+结果就一眼看出来了。
        var rows = Rows(Env(("CODEAGENT_THEME", "high-contrast")));
        Assert.Equal("high-contrast", rows["CODEAGENT_THEME"]);
        Assert.Equal("Dark", rows["Theme"]);
    }

    [Fact]
    public void UnsetValuesAreLabelledRatherThanBlank()
    {
        // 留空会看起来像"读到了空字符串"，"(未设置)" 才说得清是压根没设
        var rows = Rows(Env());
        Assert.Equal("(未设置)", rows["CODEAGENT_THEME"]);
        Assert.Equal("(未设置)", rows["CODEAGENT_ASCII"]);
        Assert.Equal("Dark", rows["Theme"]);
        Assert.Equal("off", rows["AsciiGlyphs"]);
    }

    [Fact]
    public void ExplainsWhyColoursAreOff()
    {
        Assert.Contains("NO_COLOR", Rows(Env(("NO_COLOR", "1")))["关闭原因"]);
        Assert.Contains("TERM=dumb", Rows(Env(("TERM", "dumb")))["关闭原因"]);
        Assert.Contains("重定向", Rows(Env(), redirected: true)["关闭原因"]);
        Assert.Equal("", Rows(Env())["关闭原因"]);
    }

    [Fact]
    public void ExplainsWhyAsciiIsOn()
    {
        // CODEAGENT_ASCII 只有一个能识别的值，其余一律当"想要 ASCII"——
        // 这条保守策略必须对用户可见，否则"设了却没生效"无从排查
        var rows = Rows(Env(("CODEAGENT_ASCII", "yes-please")));
        Assert.Equal("yes-please", rows["CODEAGENT_ASCII"]);
        Assert.Equal("on", rows["AsciiGlyphs"]);
    }

    [Fact]
    public void MatchesTheLiveValues()
    {
        // 自述必须与真正生效的值一致，不能各算各的。
        // 重定向状态要用**真实**的那个：测试环境里 dotnet test 本来就重定向，
        // 传一个假的 redirected:false 会让这条断言的前提和被测代码对不上。
        var saved = SafeColor.ReadEnv;
        try
        {
            SafeColor.ReadEnv = Env(("CODEAGENT_THEME", "contrast-light"), ("CODEAGENT_ASCII", "1"));
            var rows = Rows(SafeColor.ReadEnv, SafeColor.IsRedirected());
            Assert.Equal(SafeColor.Theme.ToString(), rows["Theme"]);
            Assert.Equal(SafeColor.Glyphs.AsciiEnabled ? "on" : "off", rows["AsciiGlyphs"]);
            Assert.Equal(SafeColor.Enabled ? "on" : "off", rows["Colors"]);
            Assert.Equal(SafeColor.ColorOffReason(SafeColor.ReadEnv, SafeColor.IsRedirected()), rows["关闭原因"]);
        }
        finally { SafeColor.ReadEnv = saved; }
    }

    [Fact]
    public void RowsHaveUniqueNonBlankKeys()
    {
        // /diag 用 key/value 两列渲染，重复键会覆盖成一列。
        // （注意：**等宽**的键完全没问题——CODEAGENT_THEME 和 CODEAGENT_ASCII
        // 都是 15 列，对齐良好。真正要防的是重复键。）
        var rows = SafeColor.DescribeEnvironment(Env(("CODEAGENT_THEME", "contrast")), false);
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Key)));
        var trimmed = rows.Select(r => r.Key.Trim()).ToList();
        Assert.Equal(trimmed.Count, trimmed.Distinct().Count());
    }

    [Fact]
    public void DiagActuallyShowsTheseRows()
    {
        // 源码级守卫：/diag 必须真的把自述接进输出，否则这整轮等于没接线
        var source = File.ReadAllText(FindProgram());
        Assert.Contains("DescribeEnvironment", source);
    }

    private static string FindProgram()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 1000)
                    return candidate;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
