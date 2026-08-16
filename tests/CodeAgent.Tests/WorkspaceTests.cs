using System;
using System.IO;
using System.Linq;
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
    public void Resolve_CaseVariant_MatchesFileSystemSemantics()
    {
        // 回归：Windows 大小写不敏感（../PROJ 应放行，等同根目录）；
        // Linux/macOS 大小写敏感（../PROJ 是另一目录，必须拒绝），否则沙箱可被大小写变体绕过。
        var ws = new Workspace(Root);
        var caseVariant = Path.Combine("..", Root.Split(Path.DirectorySeparatorChar).Last().ToUpperInvariant());
        if (OperatingSystem.IsWindows())
        {
            // Windows 大小写不敏感：放行（Path.GetFullPath 会保留传入的大小写，故只断言不抛异常）
            _ = ws.Resolve(caseVariant);
        }
        else
        {
            Assert.Throws<ToolException>(() => ws.Resolve(caseVariant));
        }
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
