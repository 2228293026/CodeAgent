using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 系统提示：<c>update_tasks</c> 指引 + 出厂默认的识别。
///
/// 两个问题绑在一起，因为它们是同一轮做的：
/// · 工具接好了但提示里没说 → 模型永远不调用 → <c>/tasks</c> 永远是空的
/// · 为了加这条指引改了默认提示 → 存着旧默认的老用户被判成"自定义过"
///   → 项目专属注入（ADOFAI 等）悄悄失效
/// </summary>
public sealed class SystemPromptTaskGuidanceTests
{
    [Fact]
    public void Prompt_TellsTheModelToUseUpdateTasks()
    {
        // 工具接得再完整，提示里不提，模型就不会调用——面板永远是空的
        Assert.Contains("update_tasks", AgentConfig.DefaultSystemPrompt);
    }

    [Fact]
    public void Prompt_ExplainsFullTableNotIncremental()
    {
        // 增量语义与实现相反的话，模型会以为自己加了几条、实际把整张表换掉了
        Assert.Contains("完整", AgentConfig.DefaultSystemPrompt);
    }

    [Fact]
    public void Prompt_TellsTheUserHowToSeeIt()
    {
        Assert.Contains("/tasks", AgentConfig.DefaultSystemPrompt);
    }

    [Fact]
    public void Prompt_SaysNotToBuildListsForTrivialWork()
    {
        // 一句话的事也建清单 → 面板比没有还吵
        Assert.Contains("三步", AgentConfig.DefaultSystemPrompt);
    }

    [Fact]
    public void Prompt_KeepsTheExistingRules()
    {
        foreach (var old in new[] { "stop 工具", "诚实报告", "尊重用户意图" })
            Assert.Contains(old, AgentConfig.DefaultSystemPrompt);
    }

    // —— 出厂默认的识别 ——

    [Fact]
    public void IsDefault_AcceptsTheCurrentDefault()
    {
        Assert.True(AgentConfig.IsDefaultSystemPrompt(AgentConfig.DefaultSystemPrompt));
    }

    [Fact]
    public void IsDefault_AcceptsTheLegacyDefault()
    {
        // 改了默认提示之后，老用户 codeagent.json 里存的是旧文本；
        // 全等比较会把他们判成"自定义过"，项目专属注入就悄悄失效了
        Assert.True(AgentConfig.IsDefaultSystemPrompt(AgentConfig.LegacyDefaultSystemPromptV1));
    }

    [Fact]
    public void IsDefault_TreatsNullAndBlankAsDefault()
    {
        Assert.True(AgentConfig.IsDefaultSystemPrompt(null));
        Assert.True(AgentConfig.IsDefaultSystemPrompt(""));
        Assert.True(AgentConfig.IsDefaultSystemPrompt("   \n  "));
    }

    [Fact]
    public void IsDefault_ToleratesTrailingWhitespace()
    {
        Assert.True(AgentConfig.IsDefaultSystemPrompt(AgentConfig.DefaultSystemPrompt + "\n"));
    }

    [Fact]
    public void IsDefault_RejectsARealCustomPrompt()
    {
        Assert.False(AgentConfig.IsDefaultSystemPrompt("你是一只鹦鹉"));
        Assert.False(AgentConfig.IsDefaultSystemPrompt(AgentConfig.DefaultSystemPrompt + "额外要求：用英文回答。"));
    }

    [Fact]
    public void IsDefault_RejectsAPromptThatMerelyMentionsTheDefault()
    {
        // 早年的 startsWith 写法会把"默认提示 + 用户追加"误判成默认
        Assert.False(AgentConfig.IsDefaultSystemPrompt("前缀 " + AgentConfig.DefaultSystemPrompt));
    }

    [Fact]
    public void LegacyDefault_ReallyDiffersFromTheCurrentOne()
    {
        // 两个常量长得一样的话，这条识别等于没写——测试要挡住这种"看起来在工作"的情况
        Assert.NotEqual(AgentConfig.LegacyDefaultSystemPromptV1, AgentConfig.DefaultSystemPrompt);
        Assert.False(AgentConfig.IsDefaultSystemPrompt(AgentConfig.LegacyDefaultSystemPromptV1 + "用户追加的内容"));
    }

    [Fact]
    public void Program_UsesTheHelperNotRawEquality()
    {
        // 只测 IsDefaultSystemPrompt 会漏掉"调用点还在用全等比较"这一步：
        // 改对了函数、没改调用点，等于没改
        var src = File.ReadAllText(Find("Program.cs"));
        Assert.DoesNotContain("config.SystemPrompt == AgentConfig.DefaultSystemPrompt", src);
        Assert.Contains("AgentConfig.IsDefaultSystemPrompt(", src);
    }

    [Fact]
    public void NoSourceFile_ComparesTheDefaultPromptDirectly()
    {
        var offenders = new System.Collections.Generic.List<string>();
        foreach (var f in System.IO.Directory.EnumerateFiles(SrcRoot(), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(f);
            if (text.Contains("== AgentConfig.DefaultSystemPrompt") || text.Contains("!= AgentConfig.DefaultSystemPrompt"))
                offenders.Add(Path.GetFileName(f));
        }
        Assert.True(offenders.Count == 0,
            "这些文件仍在拿默认提示做全等比较（改了默认就失效）：" + string.Join(" | ", offenders));
    }

    private static string SrcRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var d = Path.Combine(dir, "src", "CodeAgent");
                if (Directory.Exists(d) && new FileInfo(Path.Combine(d, "Program.cs")).Length > 1000)
                    return d;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new DirectoryNotFoundException("找不到 src/CodeAgent");
    }

    private static string Find(string name) => Path.Combine(SrcRoot(), name);
}
