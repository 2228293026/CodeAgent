using System;
using System.IO;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class GitInfoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gitinfo-" + Guid.NewGuid().ToString("N"));

    public GitInfoTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void InitRepo(string headContent, bool worktreePointer = false, string? pointerTarget = null)
    {
        var gitDir = Path.Combine(_dir, ".git");
        if (worktreePointer)
        {
            Directory.CreateDirectory(pointerTarget!);
            File.WriteAllText(gitDir, $"gitdir: {pointerTarget}\n");
        }
        else
        {
            Directory.CreateDirectory(gitDir);
            File.WriteAllText(Path.Combine(gitDir, "HEAD"), headContent);
        }
    }

    [Fact]
    public void CurrentBranch_SymbolicRef_ReturnsBranchName()
    {
        InitRepo("ref: refs/heads/main\n");
        Assert.Equal("main", GitInfo.CurrentBranch(_dir));
    }

    [Fact]
    public void CurrentBranch_FeatureBranchWithSlash_FullName()
    {
        InitRepo("ref: refs/heads/feature/oauth-login\n");
        Assert.Equal("feature/oauth-login", GitInfo.CurrentBranch(_dir));
    }

    [Fact]
    public void CurrentBranch_DetachedHead_ShortHashWithMarker()
    {
        InitRepo("abc1234567890def\n");
        Assert.Equal("detached:abc1234", GitInfo.CurrentBranch(_dir));
    }

    [Fact]
    public void CurrentBranch_WorktreePointerFile_ResolvesGitDir()
    {
        // worktree/submodule：.git 是文件（"gitdir: <路径>"），HEAD 在真实 gitdir 里
        var target = Path.Combine(_dir, "wt-real", ".git");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "HEAD"), "ref: refs/heads/wt-branch\n");

        var worktree = Path.Combine(_dir, "wt");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + target + "\n");

        Assert.Equal("wt-branch", GitInfo.CurrentBranch(worktree));
    }

    [Fact]
    public void CurrentBranch_NoGitDirectory_ReturnsNull()
    {
        Assert.Null(GitInfo.CurrentBranch(_dir));
    }

    [Fact]
    public void CurrentBranch_EmptyRefName_ReturnsNull()
    {
        InitRepo("ref: \n");
        Assert.Null(GitInfo.CurrentBranch(_dir));
    }

    [Fact]
    public void CurrentBranch_NonRefBareSha_TooShort_ReturnsNull()
    {
        InitRepo("abc\n"); // 损坏/异常内容：不足 7 位不显示
        Assert.Null(GitInfo.CurrentBranch(_dir));
    }
}
