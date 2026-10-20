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
