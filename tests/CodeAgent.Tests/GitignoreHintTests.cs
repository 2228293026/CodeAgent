using System;
using System.IO;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class GitignoreHintTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gihint-" + Guid.NewGuid().ToString("N"));

    public GitignoreHintTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void Init(string? gitignore)
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        Directory.CreateDirectory(Path.Combine(_dir, ".codeagent"));
        if (gitignore is not null)
            File.WriteAllText(Path.Combine(_dir, ".gitignore"), gitignore);
    }

    [Theory]
    [InlineData(null)]                 // 无 .gitignore：提示
    [InlineData("bin/\nobj/\n")]       // 忽略了别的：提示
    public void NeedsHint_WhenCodeagentNotIgnored(string? gitignore)
    {
        Init(gitignore);
        Assert.True(Program.NeedsGitignoreHint(_dir));
    }

    [Theory]
    [InlineData("foo\n.codeagent\n")]  // 无尾斜杠：不提示
    public void NoHint_WhenIgnored(string gitignore)
    {
        Init(gitignore);
        Assert.False(Program.NeedsGitignoreHint(_dir));
    }

    [Fact]
    public void NeedsHint_WhenOnlyMentionedInComment()
    {
        // 注释里出现的 .codeagent 不算忽略（前缀 #）：仍提示
        Init("# .codeagent/\nbin/\n");
        Assert.True(Program.NeedsGitignoreHint(_dir));
    }
    [Fact]
    public void NoHint_WithoutCodeagentDir()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        Assert.False(Program.NeedsGitignoreHint(_dir));
    }
}
