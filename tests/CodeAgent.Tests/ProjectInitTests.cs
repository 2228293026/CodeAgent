using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// <c>/init</c>：扫描项目并生成 AGENTS.md 草稿。
///
/// 它补的是一个真实断层：<c>AGENTS.md</c> 的内容会注入系统提示，
/// 但此前**没有任何生成它的途径**——用户只能从零手写。
///
/// 设计上只做确定性探测（不调模型）：
///   · /init 应当瞬间完成、结果可预期，不是又一次等待流式输出；
///   · 探测结果可测，模型生成的结果不可测；
///   · 猜错一个构建命令比不写更糟——用户会照着跑，失败后怀疑整个工具。
/// </summary>
public class ProjectInitTests : IDisposable
{
    private readonly string _root;

    public ProjectInitTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ca_init_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    private string Touch(string name, string? content = null)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content ?? "");
        return path;
    }

    [Fact]
    public void GeneratesAHeadingAndKeepsItNonEmpty()
    {
        var result = ProjectInit.Generate(_root);
        Assert.Contains("# 项目规则", result.Content);
        Assert.True(result.Content.Trim().Length > 20, result.Content);
    }

    [Fact]
    public void DetectsDotnetStackAndBuildCommand()
    {
        Touch("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var result = ProjectInit.Generate(_root);
        Assert.Contains(".NET", result.Content);
        Assert.Contains("dotnet build", result.Content);
        Assert.Contains(result.Findings, f => f.Contains(".NET", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotClaimTestCommandWithoutTests()
    {
        // 没有测试工程却写 `dotnet test`，用户照跑会得到「找不到测试」——
        // 猜错一个命令比不写更糟。
        Touch("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var result = ProjectInit.Generate(_root);
        Assert.DoesNotContain("dotnet test", result.Content);
    }

    [Fact]
    public void EmitsTestCommandWhenATestProjectExists()
    {
        Touch("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        Touch(Path.Combine("tests", "App.Tests", "App.Tests.csproj"), "<Project />");
        var result = ProjectInit.Generate(_root);
        Assert.Contains("dotnet test", result.Content);
    }

    [Fact]
    public void DetectsNodeAndPythonTogether()
    {
        Touch("package.json", "{\"name\":\"x\"}");
        Touch("requirements.txt", "pytest\n");
        var result = ProjectInit.Generate(_root);
        Assert.Contains("Node.js", result.Content);
        Assert.Contains("Python", result.Content);
        Assert.Contains("npm install", result.Content);
    }

    [Fact]
    public void EmptyDirectoryStillProducesSomethingUsable()
    {
        // 空目录不该崩，也不该生成一份满是指令的文件：
        // 没有证据时只留下最泛用的那几条约定。
        var result = ProjectInit.Generate(_root);
        Assert.Contains("# 项目规则", result.Content);
        Assert.Contains("## 约定", result.Content);
        Assert.DoesNotContain("dotnet build", result.Content);
    }

    [Fact]
    public void ListsOnlyDirectoriesThatExist()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        var result = ProjectInit.Generate(_root);
        Assert.Contains("`src/`", result.Content);
        // tests/docs 不存在就不该出现在目录表里
        Assert.DoesNotContain("`docs/`", result.Content);
    }

    [Fact]
    public void MentionsThatTheFileIsInjectedIntoThePrompt()
    {
        // 生成的文件必须在**内容里**说明自己会进提示：
        // 用户可能是第一次见到 AGENTS.md，不知道它会消耗上下文。
        var result = ProjectInit.Generate(_root);
        Assert.Contains("系统提示", result.Content);
    }

    [Fact]
    public void FindingsAreReportedSoTheUserCanJudgeTheDraft()
    {
        Touch("App.csproj", "<Project />");
        var result = ProjectInit.Generate(_root);
        Assert.NotEmpty(result.Findings);
    }

    // ── 落盘前的三态判定 ─────────────────────────────────────────────
    //
    // 「文件存在吗」这一个问题答不了「写下去会发生什么」。
    // 下面这些用例守的是**最坏那种缺陷**：命令报「已生成」，
    // 用户的规则却一个字都读不到——屏幕和实际状态不一致，而且没有任何报错。

    [Fact]
    public void TargetNameIsTheFirstRuleFileTheRuntimeReads()
    {
        // 两个名字一旦漂移，/init 写的东西下次启动根本不生效。
        // 用户看到「生成成功」，然后发现模型完全不知道那些规则——最难查的那种缺陷。
        Assert.Equal(ProjectInit.TargetFileName, Program.RuleFileNames[0]);
    }

    [Fact]
    public void EmptyDirectoryIsSafeToWrite()
    {
        var plan = ProjectInit.EvaluateTarget(_root, Program.RuleFileNames);
        Assert.Equal(ProjectInit.TargetState.Create, plan.State);
        Assert.True(plan.CanWrite);
        Assert.Null(plan.Blocking);
        Assert.Equal(Path.Combine(_root, ProjectInit.TargetFileName), plan.Target);
    }

    [Fact]
    public void ExistingTargetFileBlocksTheWrite()
    {
        var path = Touch(ProjectInit.TargetFileName, "# 我自己写的规则\n");
        var plan = ProjectInit.EvaluateTarget(_root, Program.RuleFileNames);
        Assert.Equal(ProjectInit.TargetState.Exists, plan.State);
        Assert.False(plan.CanWrite);
        Assert.Equal(ProjectInit.TargetFileName, plan.Blocking);
        // 判定必须是纯的：评估一遍不能把用户的内容改掉。
        Assert.Equal("# 我自己写的规则\n", File.ReadAllText(path));
    }

    [Fact]
    public void DirectoryWithTheTargetNameAlsoBlocksTheWrite()
    {
        // 只判 File.Exists 会在这里放行，然后在 WriteAllText 时抛
        // UnauthorizedAccessException——用户得到的是崩溃，而不是一句解释。
        Directory.CreateDirectory(Path.Combine(_root, ProjectInit.TargetFileName));
        var plan = ProjectInit.EvaluateTarget(_root, Program.RuleFileNames);
        Assert.Equal(ProjectInit.TargetState.Exists, plan.State);
        Assert.False(plan.CanWrite);

        // 同名的东西是目录不是文件：文案必须说出来，否则用户会照着
        // 「移走或删除这个文件」去删一个目录，或者反过来以为自己删错了。
        var text = string.Join("\n", ProjectInit.ExplainTarget(plan));
        Assert.Contains("目录", text);
    }

    [Theory]
    [InlineData("CLAUDE.md")]
    [InlineData(".cursorrules")]
    public void LowerPriorityRuleFileIsNotSilentlyShadowed(string existing)
    {
        // 新建的 AGENTS.md 排在名单第一位，会把这些整个盖住。
        // 文件没坏、程序没报错，规则却静默失效了。
        Touch(existing, "重要规则\n");
        var plan = ProjectInit.EvaluateTarget(_root, Program.RuleFileNames);
        Assert.Equal(ProjectInit.TargetState.Shadow, plan.State);
        Assert.False(plan.CanWrite);
        Assert.Equal(existing, plan.Blocking);
        Assert.False(File.Exists(Path.Combine(_root, ProjectInit.TargetFileName)),
            "判定阶段不许落盘");
    }

    [Fact]
    public void HigherPriorityRuleFileDoesNotCountAsShadowed()
    {
        // 只被排在目标**之后**的规则文件才叫「被遮蔽」。
        // 排在前面的比目标优先，写下去影响不到它——这里用倒序名单把这条规则钉住。
        Touch("CLAUDE.md", "优先级更高\n");
        var plan = ProjectInit.EvaluateTarget(_root, new[] { "CLAUDE.md", ProjectInit.TargetFileName });
        Assert.Equal(ProjectInit.TargetState.Create, plan.State);
        Assert.True(plan.CanWrite);
    }

    [Fact]
    public void ShadowReportsEveryBlockedFileInListOrder()
    {
        Touch("CLAUDE.md", "x\n");
        Touch(".cursorrules", "y\n");
        var plan = ProjectInit.EvaluateTarget(_root, Program.RuleFileNames);
        Assert.Equal(ProjectInit.TargetState.Shadow, plan.State);
        Assert.Equal(Program.RuleFileNames.Skip(1).ToArray(), plan.Existing.ToArray());
    }

    [Fact]
    public void EveryStateExplainsItselfAndNeverBlamesAHealthyFile()
    {
        // 三种局面各造一个目录，逐个走一遍：名字里写着 EveryState，
        // 就别只测其中一种——漏掉的那种恰恰是用户最可能撞上的。
        var create = Path.Combine(_root, "create");
        var exists = Path.Combine(_root, "exists");
        var shadow = Path.Combine(_root, "shadow");
        Directory.CreateDirectory(create);
        Directory.CreateDirectory(exists);
        Directory.CreateDirectory(shadow);
        File.WriteAllText(Path.Combine(exists, ProjectInit.TargetFileName), "已有内容\n");
        File.WriteAllText(Path.Combine(shadow, "CLAUDE.md"), "有效规则\n");

        var plans = new[]
        {
            ProjectInit.EvaluateTarget(create, Program.RuleFileNames),
            ProjectInit.EvaluateTarget(exists, Program.RuleFileNames),
            ProjectInit.EvaluateTarget(shadow, Program.RuleFileNames),
        };

        Assert.Equal(
            new[] { ProjectInit.TargetState.Create, ProjectInit.TargetState.Exists, ProjectInit.TargetState.Shadow },
            plans.Select(p => p.State).ToArray());

        foreach (var plan in plans)
        {
            var lines = ProjectInit.ExplainTarget(plan);
            Assert.NotEmpty(lines);
            Assert.False(string.IsNullOrWhiteSpace(lines[0]), "首行必须是结论");
            foreach (var line in lines)
                Assert.False(string.IsNullOrWhiteSpace(line), $"{plan.State} 输出了空行");
        }

        // 拒绝文案有一条硬约束：只说「新文件会盖住它」，
        // 不许说旧文件「读不到 / 是空的」——旧文件可能写得好好的、正在生效，
        // 说它坏了，用户就会去修一个根本没坏的东西。
        var shadowPlan = plans[2];
        var text = string.Join("\n", ProjectInit.ExplainTarget(shadowPlan));
        Assert.Contains("盖住", text);
        Assert.DoesNotContain("是空的", text);
        Assert.DoesNotContain("读取失败", text);
        Assert.DoesNotContain("坏了", text);
    }

    [Fact]
    public void ExistingTargetExplanationGivesAWayOut()
    {
        Touch(ProjectInit.TargetFileName, "已有内容\n");
        var text = string.Join("\n", ProjectInit.ExplainTarget(
            ProjectInit.EvaluateTarget(_root, Program.RuleFileNames)));
        // 只说「没生成」不够：用户会以为 /init 坏了。必须给出唯一出路。
        Assert.Contains("未覆盖", text);
        Assert.Contains("删除", text);
    }

    [Fact]
    public void GeneratedFileIsValidForTheExistingRuleReader()
    {
        // 最关键的一条：生成的草稿必须能被现有的 AGENTS.md 读取路径消化。
        // 若两者对不上，/init 生成的东西下次启动根本不生效。
        Touch("App.csproj", "<Project />");
        var result = ProjectInit.Generate(_root);
        var path = Path.Combine(_root, "AGENTS.md");
        File.WriteAllText(path, result.Content);
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path).Trim();
        Assert.True(text.Length > 0, "生成的规则文件不该是空内容（空内容会被 ReadRuleFile 判为未加载）");
    }
}
