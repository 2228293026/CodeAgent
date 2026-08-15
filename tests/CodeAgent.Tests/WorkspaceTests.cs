using System;
using System.IO;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public class WorkspaceTests
{
    // 跨平台：用临时目录代替硬编码的 Windows 路径（CI 在 Linux 上跑）
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "codeagent-ws-test");

    [Fact]
    public void Resolve_NullOrEmpty_ReturnsRoot()
    {
        var ws = new Workspace(Root);
        Assert.Equal(Path.GetFullPath(Root), ws.Resolve(null));
        Assert.Equal(Path.GetFullPath(Root), ws.Resolve(""));
    }

    [Fact]
    public void Resolve_InsideRoot_ReturnsFullPath()
    {
        var ws = new Workspace(Root);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Root, "src", "a.cs")),
            ws.Resolve(Path.Combine("src", "a.cs")));
    }

    [Fact]
    public void Resolve_Dot_ReturnsRoot()
    {
        var ws = new Workspace(Root);
        Assert.Equal(Path.GetFullPath(Root), ws.Resolve("."));
    }

    [Fact]
    public void Resolve_Escape_Throws()
    {
        var ws = new Workspace(Root);
        Assert.Throws<ToolException>(() => ws.Resolve(Path.Combine("..", "secret.txt")));
    }

    [Fact]
    public void Resolve_SiblingPrefix_Rejected()
    {
        // 同级前缀目录（如 proj2）不应被 proj 的沙箱放行
        var ws = new Workspace(Root);
        Assert.Throws<ToolException>(() => ws.Resolve(Path.Combine("..", "proj2", "x.txt")));
    }

    [Fact]
    public void ToRelative_ConvertsBack()
    {
        var ws = new Workspace(Root);
        Assert.Equal(
            Path.Combine("src", "a.cs"),
            ws.ToRelative(Path.GetFullPath(Path.Combine(Root, "src", "a.cs"))));
    }
}
