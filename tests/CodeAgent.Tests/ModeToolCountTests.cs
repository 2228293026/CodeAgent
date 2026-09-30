using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// <c>/mode</c> 列表里每个模式**实际可用**的工具数。
///
/// 231 个工具分给 8 种模式，用户问的却是"这个模式能不能干这事"。
/// 光看说明（「只读分析项目」）判断不出 plan 到底能不能跑命令——
/// 而这个差别直接决定了他该不该切过去。
/// 实测加之前：plan/explain/review 三个只读模式与 code 的差别**完全看不出来**。
/// </summary>
public sealed class ModeToolCountTests
{
    private static AgentConfig Config() => new();

    [Fact]
    public void WithoutACountProvider_NoSuffixIsShown()
    {
        // 旧调用方（测试、其它入口）不该突然多出后缀
        var text = Program.ModeListText(Config(), "code");
        Assert.DoesNotContain("个工具", text);
    }

    [Fact]
    public void EachModeGetsItsOwnCount()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["code"] = 231,
            ["plan"] = 6,
        };
        var text = Program.ModeListText(Config(), "code", m => counts.TryGetValue(m, out var n) ? n : 0);
        Assert.Contains("· 231 个工具", text);
        Assert.Contains("· 6 个工具", text);
    }

    [Fact]
    public void ZeroCountShowsNoSuffix()
    {
        // 查不到就**不加**后缀：显示「0 个工具」会让人以为这个模式什么都干不了
        var text = Program.ModeListText(Config(), "code", _ => 0);
        Assert.DoesNotContain("个工具", text);
    }

    [Fact]
    public void CurrentModeMarkerSurvivesTheSuffix()
    {
        var text = Program.ModeListText(Config(), "plan", _ => 6);
        var planLine = text.Split('\n').First(l => l.Contains("plan"));
        Assert.Contains(SafeColor.Glyphs.Left, planLine);
        Assert.Contains("6 个工具", planLine);
        // 标记在末尾，说明与计数都不该把它挤掉
        Assert.EndsWith(SafeColor.Glyphs.Left, planLine.TrimEnd());
    }

    [Fact]
    public void ReadOnlyModesReallyAreMuchSmaller()
    {
        // 与 ToolsForMode 同一套口径：只读模式的可用工具应当**远少于**全量。
        // 防止哪天有人把 AllowedTools 弄丢了，模式静默变成"什么都能干"——
        // 那是权限问题，不是显示问题。
        var modes = Modes.Build(Config());
        var plan = modes.First(m => m.Name == "plan");
        Assert.NotNull(plan.AllowedTools);
        Assert.True(plan.AllowedTools!.Length < 20, $"plan 允许了 {plan.AllowedTools.Length} 个工具，只读模式不该这么多");
    }

    [Fact]
    public void TheCommandPassesARealCounter()
    {
        // 只测 ModeListText 会漏掉"/mode 压根没传计数器"——那正是原来没有数字的原因
        var src = ReadSource(FindProgram());
        Assert.Contains("ModeListText(config, agent.CurrentMode.Name, name =>", src);
        Assert.Contains("AgentClass.AlwaysAvailableTool", src);
    }

    [Fact]
    public void TheCounterMatchesToolsForModeSemantics()
    {
        // 计数必须复刻 ToolsForMode 的规则（含 update_tasks 这个"每种模式都有"的例外），
        // 否则"231 vs 6"和 /tools 里的实际条数对不上，用户会以为其中一个在骗人
        var src = ReadSource(FindAgent());
        Assert.Contains("AlwaysAvailableTool", src);
        var tools = ReadSource(FindProgram());
        Assert.Contains("AlwaysAvailableTool", tools);
    }

    private static string ReadSource(string p) => File.ReadAllText(p).Replace("\r\n", "\n");

    private static string FindProgram() => Find("Program.cs");
    private static string FindAgent() => Find(Path.Combine("Agent", "Agent.cs"));

    private static string Find(params string[] parts)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            for (var i = 0; i < 10 && d.Length > 1; i++)
            {
                var f = Path.Combine(new[] { d }.Concat(new[] { "src", "CodeAgent" }).Concat(parts).ToArray());
                if (File.Exists(f))
                    return f;
                var parent = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == d)
                    break;
                d = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/" + Path.Combine(parts));
    }
}
