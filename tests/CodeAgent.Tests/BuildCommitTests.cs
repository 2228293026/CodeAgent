using System;
using System.IO;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// /diag 里的构建标识。
///
/// 版本号逐轮不变，光看版本号无法判断跑的是不是最新构建——反馈 bug 时最费时间的
/// 恰恰是「对方还在旧二进制上」。这条把 sha 捡回来并收进 /diag。
/// </summary>
public sealed class BuildCommitTests
{
    [Fact]
    public void Commit_IsExtractedFromInformationalVersion()
    {
        Assert.Equal("852644f", Program.ShortenCommit("1.0.0+852644ff219e6a0c32b8bf4d8d2c75bfff00f50b"));
    }

    [Fact]
    public void Commit_IsShortenedToSevenChars()
    {
        Assert.Equal(7, Program.ShortenCommit("1.0.0+852644ff219e6a0c32b8bf4d8d2c75bfff00f50b").Length);
    }

    [Fact]
    public void Commit_KeepsShortShasIntact()
    {
        // 短于 7 位不该被截成 7 位（那会补出空位）
        Assert.Equal("abc1234", Program.ShortenCommit("1.0.0+abc1234"));
        Assert.Equal("abc", Program.ShortenCommit("1.0.0+abc"));
    }

    [Fact]
    public void Commit_StripsSourceLinkSuffix()
    {
        Assert.Equal("852644f", Program.ShortenCommit("1.0.0+852644ff219e.sha.abc"));
    }

    [Fact]
    public void Commit_AbsentVersionYieldsEmpty()
    {
        // 本地构建常常没有 sha。这时**不显示假值**，而不是显示一段编出来的标识
        Assert.Equal(string.Empty, Program.ShortenCommit("1.0.0"));
        Assert.Equal(string.Empty, Program.ShortenCommit(null));
        Assert.Equal(string.Empty, Program.ShortenCommit(""));
        Assert.Equal(string.Empty, Program.ShortenCommit("1.0.0+"));
    }

    [Fact]
    public void Commit_IsExposedOnProgram()
    {
        // /diag 读的就是这个属性；测试进程里大概率没有 sha，只断言"不会抛且长度合法"
        var c = Program.BuildCommit;
        Assert.True(c.Length <= 7);
        Assert.DoesNotContain('+', c);
    }

    [Fact]
    public void Commit_IsWiredIntoDiag()
    {
        // 只测纯函数会漏掉「接进 /diag」这一步
        var source = System.IO.File.ReadAllText(FindProgram());
        Assert.Contains("\"Build commit\"", source);
    }

    private static string FindProgram()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent");
                var program = Path.Combine(candidate, "Program.cs");
                if (Directory.Exists(candidate) && File.Exists(program) && new FileInfo(program).Length > 1000)
                    return program;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
