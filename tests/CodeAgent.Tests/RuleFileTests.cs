using System;
using System.IO;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 规则文件（AGENTS.md / CLAUDE.md / .cursorrules）的内容必须**真的进上下文**。
///
/// 修的是一个名不副实的缺陷：横幅一直打印「已加载 AGENTS.md: 路径」，
/// 而 <c>RuleFile</c> 只返回路径——全项目没有任何一处读过它的内容。
/// 用户按约定把项目规则写进 AGENTS.md，模型却完全看不到，
/// 比「不支持这个功能」更糟：用户会以为规则生效了，只是模型不听话。
/// </summary>
public class RuleFileTests : IDisposable
{
    private readonly string _dir;

    public RuleFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "codeagent-ruletest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ReadsTheContentNotJustThePath()
    {
        var path = Path.Combine(_dir, "AGENTS.md");
        File.WriteAllText(path, "# 项目规则\n\n提交前必须跑 dotnet test。");
        var content = Program.ReadRuleFile(path);
        Assert.NotNull(content);
        Assert.Contains("提交前必须跑 dotnet test", content);
    }

    [Fact]
    public void EmptyFileYieldsNull()
    {
        // 空文件不该往系统提示里塞一个空的「项目规则」块——那是纯噪音
        var path = Path.Combine(_dir, "AGENTS.md");
        File.WriteAllText(path, "   \n\n  ");
        Assert.Null(Program.ReadRuleFile(path));
    }

    [Fact]
    public void MissingFileYieldsNullInsteadOfThrowing()
    {
        // 规则文件读不到只该少一段上下文，绝不该让程序起不来
        Assert.Null(Program.ReadRuleFile(Path.Combine(_dir, "nope.md")));
    }

    [Fact]
    public void OversizedFileIsTruncatedWithHeadAndTail()
    {
        // 有人把整份 API 文档贴进 AGENTS.md：几十万字符会直接吃掉整个窗口，
        // 于是「帮我看个 bug」都还没说就没上下文了。
        var head = "HEAD-MARKER " + new string('a', 40_000);
        var tail = new string('b', 40_000) + " TAIL-MARKER";
        var path = Path.Combine(_dir, "AGENTS.md");
        File.WriteAllText(path, head + "\n" + tail);
        var content = Program.ReadRuleFile(path);
        Assert.NotNull(content);
        // 头尾都要留：只保开头会丢掉文件末尾最常放的「禁止事项」
        Assert.Contains("HEAD-MARKER", content);
        Assert.Contains("TAIL-MARKER", content);
        // 必须有交代：静默截断会让用户以为规则完整生效了
        Assert.Contains("已保留头尾", content);
        Assert.True(content.Length < 80_100, $"截断后仍有 {content.Length} 字符");
    }

    [Fact]
    public void RuleFilePrefersAgentsMd()
    {
        // 三个候选同时存在时取 AGENTS.md：它是这个项目的约定名
        File.WriteAllText(Path.Combine(_dir, "AGENTS.md"), "a");
        File.WriteAllText(Path.Combine(_dir, "CLAUDE.md"), "c");
        File.WriteAllText(Path.Combine(_dir, ".cursorrules"), "r");
        var found = Program.RuleFile(_dir);
        Assert.NotNull(found);
        Assert.Equal("AGENTS.md", Path.GetFileName(found));
    }

    [Fact]
    public void FallsBackToClaudeMdThenCursorRules()
    {
        // 兼容另外两份约定：用户从 Claude Code / Cursor 迁移过来时文件已经在那儿了
        File.WriteAllText(Path.Combine(_dir, "CLAUDE.md"), "c");
        Assert.Equal("CLAUDE.md", Path.GetFileName(Program.RuleFile(_dir)));
        File.Delete(Path.Combine(_dir, "CLAUDE.md"));
        File.WriteAllText(Path.Combine(_dir, ".cursorrules"), "r");
        Assert.Equal(".cursorrules", Path.GetFileName(Program.RuleFile(_dir)));
    }

    [Fact]
    public void NoRuleFileYieldsNull()
    {
        Assert.Null(Program.RuleFile(_dir));
    }
}
