using System;
using System.IO;
using System.Linq;
using CodeAgent;
using CodeAgent.Tools;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>输出风格：加载、覆盖规则、注入系统提示。</summary>
public sealed class OutputStylesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ca_style_" + Guid.NewGuid().ToString("N"));
    private readonly string _userRoot;

    public OutputStylesTests()
    {
        _userRoot = Path.Combine(_root, "home");
        Directory.CreateDirectory(Path.Combine(_root, "project"));
        Directory.CreateDirectory(_userRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Project => Path.Combine(_root, "project");

    private void WriteStyle(string name, string body, bool project = true, string? description = null)
    {
        var dir = project ? OutputStyles.ProjectDirectory(Project) : OutputStyles.UserDirectory(_userRoot);
        Directory.CreateDirectory(dir);
        var text = description is null ? body : "---\ndescription: " + description + "\n---\n" + body;
        File.WriteAllText(Path.Combine(dir, name + ".md"), text);
    }

    [Fact]
    public void BuiltinStylesAreAlwaysThereAndDefaultIsEmpty()
    {
        var library = OutputStyles.Load(Project, _userRoot);

        Assert.Empty(library.Problems);
        var names = library.Styles.Select(s => s.Name).ToArray();
        Assert.Contains("default", names);
        Assert.Contains("explanatory", names);
        Assert.Contains("learning", names);
        // default 必须是「什么都不加」：否则每个没开风格的会话都被悄悄换了行为
        Assert.Equal(string.Empty, OutputStyles.Find(library, "default")!.Body);
    }

    [Fact]
    public void CustomStyleReadsFrontMatterDescriptionAndBody()
    {
        WriteStyle("concise", "只给结论，不写铺垫。\n", description: "短句优先");

        var style = OutputStyles.Find(OutputStyles.Load(Project, _userRoot), "concise");

        Assert.NotNull(style);
        Assert.Equal("短句优先", style!.Description);
        Assert.Equal("只给结论，不写铺垫。", style.Body);
        Assert.False(style.IsBuiltin);
        Assert.True(style.IsProject);
    }

    [Fact]
    public void ProjectStyleOverridesUserStyleOfTheSameName()
    {
        WriteStyle("concise", "用户级版本", project: false);
        WriteStyle("concise", "项目级版本");

        var style = OutputStyles.Find(OutputStyles.Load(Project, _userRoot), "concise");

        Assert.Equal("项目级版本", style!.Body);
        Assert.True(style.IsProject);
    }

    [Fact]
    public void BuiltinNameCannotBeShadowedByAFile()
    {
        // 允许覆盖 default 就等于「用户以为自己什么都没配，其实行为被换了」
        WriteStyle("default", "把每句话都翻译成英文");

        var library = OutputStyles.Load(Project, _userRoot);

        Assert.Equal(string.Empty, OutputStyles.Find(library, "default")!.Body);
        Assert.Contains(library.Problems, p => p.Contains("内置"));
    }

    [Fact]
    public void StyleWithEmptyBodyIsSkippedWithReason()
    {
        var dir = OutputStyles.ProjectDirectory(Project);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "blank.md"), "---\ndescription: 只有描述\n---\n   \n");

        var library = OutputStyles.Load(Project, _userRoot);

        Assert.Null(OutputStyles.Find(library, "blank"));
        Assert.Contains(library.Problems, p => p.Contains("正文为空"));
    }

    [Fact]
    public void FindIsCaseInsensitiveAndUnknownReturnsNull()
    {
        WriteStyle("Concise", "短");
        var library = OutputStyles.Load(Project, _userRoot);

        Assert.NotNull(OutputStyles.Find(library, "CONCISE"));
        Assert.Null(OutputStyles.Find(library, "no-such-style"));
        // 空名字回落到 default：配置文件里没写这一项时不该变成未知风格
        Assert.Equal("default", OutputStyles.Find(library, null)!.Name);
    }

    [Fact]
    public void AppendLeavesTheBasePromptUntouchedWhenTheStyleIsEmpty()
    {
        Assert.Equal("基础提示", OutputStyles.Append("基础提示", null));
        Assert.Equal("基础提示", OutputStyles.Append("基础提示", "   "));
    }

    [Fact]
    public void AppendPutsTheStyleAfterTheBasePrompt()
    {
        var merged = OutputStyles.Append("基础提示", "每段先给结论。");

        Assert.StartsWith("基础提示", merged);
        Assert.Contains("每段先给结论。", merged);
        // 顺序反了就等于让风格覆盖工具用法，模型会开始「不调工具只解释」
        Assert.True(merged.IndexOf("基础提示", StringComparison.Ordinal) < merged.IndexOf("每段先给结论。", StringComparison.Ordinal));
    }

    [Fact]
    public void StyleListMarksTheCurrentOne()
    {
        WriteStyle("concise", "短");
        var library = OutputStyles.Load(Project, _userRoot);

        var lines = Program.FormatOutputStyleList(library, "concise", 100);

        Assert.Contains(lines, l => l.Contains("* concise"));
        Assert.Contains(lines, l => l.Contains("explanatory") && l.StartsWith(" ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("/output-style"));
    }

    [Fact]
    public void StylePromptReachesTheAgentSystemPrompt()
    {
        // 这是「配了不生效」的唯一真证据：风格正文必须真的出现在系统提示里
        var config = new AgentConfig
        {
            SaveSessions = false,
            SessionDir = Path.Combine(_root, "sessions"),
            SessionOnlyStylePrompt = "每段先给结论。",
        };
        var agent = new AgentClass(config, new FakeProvider(), ToolRegistry.CreateDefault(), Project);

        Assert.Contains("每段先给结论。", agent.CurrentSystemPrompt);
    }

    [Fact]
    public void WithoutAStyleTheSystemPromptIsUnchanged()
    {
        var config = new AgentConfig
        {
            SaveSessions = false,
            SessionDir = Path.Combine(_root, "sessions"),
        };
        var agent = new AgentClass(config, new FakeProvider(), ToolRegistry.CreateDefault(), Project);

        Assert.Equal(config.SystemPrompt, agent.CurrentSystemPrompt);
        Assert.DoesNotContain("[输出风格]", agent.CurrentSystemPrompt);
    }
}
