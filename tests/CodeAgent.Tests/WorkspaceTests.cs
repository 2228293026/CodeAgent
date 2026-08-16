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
    public void Resolve_AbsolutePathOutside_IsRejected()
    {
        // 绝对路径（如 C:\Windows 或 /etc）即使以合法相对形式传入也应被拒绝
        var ws = new Workspace(Root);
        var absolute = Path.Combine(Path.GetTempPath(), "codeagent-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(absolute);
        try
        {
            Assert.Throws<ToolException>(() => ws.Resolve(absolute));
        }
        finally
        {
            try { Directory.Delete(absolute, true); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void Resolve_InvalidPathChars_ThrowsToolException()
    {
        // 回归：含 NUL 等非法字符的路径曾让 Path.GetFullPath 抛裸 ArgumentException，
        // 应转为清晰的 ToolException
        var ws = new Workspace(Root);
        var ex = Assert.Throws<ToolException>(() => ws.Resolve("bad\0name.txt"));
        Assert.Contains("路径非法", ex.Message);
    }

    [Fact]
    public void Resolve_SymlinkEscape_IsRejected()
    {
        // 回归：工作区内的符号链接指向外部时，应通过链接解析后的真实路径拦截越界
        var outside = Path.Combine(Path.GetTempPath(), "codeagent-link-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var wsRoot = Path.Combine(Path.GetTempPath(), "codeagent-link-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wsRoot);
        try
        {
            var link = Path.Combine(wsRoot, "escape");
            try
            {
                Directory.CreateSymbolicLink(link, outside); // 不支持的平台/无权限时跳过
            }
            catch
            {
                return; // 无符号链接权限（如 Windows 非开发者模式）：跳过
            }
            var ws = new Workspace(wsRoot);
            Assert.Throws<ToolException>(() => ws.Resolve(Path.Combine("escape", "secret.txt")));
        }
        finally
        {
            try { Directory.Delete(wsRoot, true); } catch { /* 忽略 */ }
            try { Directory.Delete(outside, true); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void Resolve_SymlinkInside_IsAllowed()
    {
        // 符号链接指向工作区内时不应误拦
        var wsRoot = Path.Combine(Path.GetTempPath(), "codeagent-link-ws2-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(wsRoot, "real");
        Directory.CreateDirectory(target);
        try
        {
            var link = Path.Combine(wsRoot, "alias");
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch
            {
                return; // 无权限：跳过
            }
            var ws = new Workspace(wsRoot);
            _ = ws.Resolve(Path.Combine("alias", "x.txt")); // 不应抛异常
        }
        finally
        {
            try { Directory.Delete(wsRoot, true); } catch { /* 忽略 */ }
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
