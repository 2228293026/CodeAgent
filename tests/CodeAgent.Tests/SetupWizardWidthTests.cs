using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 配置向导的**收口守卫**。
///
/// 前面几轮把 SetupWizard 的提问行、选项行、字段行、提示行都改成了宽度受限的渲染，
/// 但那是一个一个改的。留一个守卫，让"往向导里加一行新的裸插值输出"必须先想清楚宽度——
/// 否则下一轮又会有一处 `WriteLine($"…{userValue}…")` 悄悄躺在那里。
/// </summary>
public sealed class SetupWizardWidthTests
{
    private static string Source() => File.ReadAllText(Find());

    private static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = Path.Combine(dir, "src", "CodeAgent", "SetupWizard.cs");
                if (File.Exists(f) && new FileInfo(f).Length > 1000)
                    return f;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/SetupWizard.cs");
    }

    /// <summary>会插入"用户可控长度内容"的表达式片段。命中即视为需要宽度预算。</summary>
    private static readonly string[] UnboundedContent =
    [
        "{path}", "{name}", "{ex.Message}", "{opts.Model}", "{existing.Model}",
        "{p.Env}", "{p.BaseUrl}", "{opts.ApiKeyEnv}", "{value}", "{options[i]",
        "{Presets[i]", "{extras[i]", "{opts.Model}",
    ];

    [Fact]
    public void Wizard_HasNoUnboundedInterpolatedOutput()
    {
        var offenders = new System.Collections.Generic.List<string>();
        var lines = Source().Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (!t.StartsWith("output.Write", StringComparison.Ordinal) || !t.Contains("$\"", StringComparison.Ordinal))
                continue;
            if (!UnboundedContent.Any(u => t.Contains(u, StringComparison.Ordinal)))
                continue;
            // 已在同一行/下一行走宽度受限的渲染函数
            var window = i + 1 < lines.Length ? t + " " + lines[i + 1].Trim() : t;
            if (window.Contains("FormatFieldLine")
                || window.Contains("FormatOptionLine")
                || window.Contains("FormatNoticeLine")
                || window.Contains("FormatAskPrompt")
                || window.Contains("FormatWizardBanner")
                || window.Contains("Notice("))
                continue;
            offenders.Add($"SetupWizard.cs:{i + 1} {t}");
        }
        Assert.True(offenders.Count == 0,
            "向导里这些输出行没有走宽度受限的渲染函数：" + string.Join(" | ", offenders));
    }

    [Fact]
    public void Wizard_UsesTheSharedFormatters()
    {
        // 复用而不是各写一份：另写一份只会在这套约定之外再多一个可能超宽的地方
        var src = Source();
        Assert.Contains("FormatFieldLine(", src);
        Assert.Contains("FormatOptionLine(", src);
        Assert.Contains("FormatAskPrompt(", src);
        Assert.Contains("FormatWizardBanner(", src);
        Assert.Contains("Program.FormatNoticeLine(", src);
    }

    [Fact]
    public void Banner_NeverExceedsWidth()
    {
        foreach (var w in Enumerable.Range(1, 200))
        {
            var b = SetupWizard.FormatWizardBanner(w);
            var actual = TextUtil.DisplayWidth(b);
            Assert.True(actual <= w, $"w={w} 实际宽 {actual}: [{b}]");
        }
    }

    [Fact]
    public void Banner_KeepsTheTitleWhenItFits()
    {
        Assert.Contains("供应商配置向导", SetupWizard.FormatWizardBanner(120));
    }

    [Fact]
    public void Banner_DropsDecorationBeforeTitle()
    {
        // 装饰是装饰，标题才是信息。窄到放不下装饰时必须先丢装饰——
        // 标题「CodeAgent 供应商配置向导」本身 24 列，19 列以内无论如何都放不全，
        // 所以这里断言的是"装饰没了、剩下的都是标题内容"，而不是"标题完整"。
        for (var w = 1; w <= 19; w++)
        {
            var b = SetupWizard.FormatWizardBanner(w);
            Assert.DoesNotContain(SafeColor.Glyphs.RuleChar, b);
            Assert.DoesNotContain(SafeColor.Glyphs.Rule2, b);
            Assert.False(string.IsNullOrWhiteSpace(b), $"w={w} 标题被整条丢掉了");
        }
    }

    [Fact]
    public void PasteableCommands_AreNeverTruncated()
    {
        // 可粘贴的命令被截一半，粘进 shell 直接报错——与普通输出相反
        var cmd = SetupWizard.EnvCommand("powershell", "CODEAGENT_THEME", "contrast");
        Assert.Equal("$env:CODEAGENT_THEME=\"contrast\"", cmd);
        var long1 = SetupWizard.EnvCommand("powershell", "CODEAGENT_THEME", new string('v', 300));
        Assert.Equal(long1, SetupWizard.EnvCommand("powershell", "CODEAGENT_THEME", new string('v', 300)));
    }

    [Fact]
    public void CopyableCommands_LiveOnTheirOwnLine()
    {
        // 标签与命令同行的话，命令只剩"终端宽度 - 标签宽度"
        var src = Source();
        Assert.DoesNotContain("PowerShell: {EnvCommand", src);
        Assert.DoesNotContain("cmd.exe:    {EnvCommand", src);
    }
}
