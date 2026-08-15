using System;
using System.IO;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public class WorkspaceTests
{
    [Fact]
    public void Resolve_NullOrEmpty_ReturnsRoot()
    {
        var ws = new Workspace(@"C:\proj");
        Assert.Equal(Path.GetFullPath(@"C:\proj"), ws.Resolve(null));
        Assert.Equal(Path.GetFullPath(@"C:\proj"), ws.Resolve(""));
    }

    [Fact]
    public void Resolve_InsideRoot_ReturnsFullPath()
    {
        var ws = new Workspace(@"C:\proj");
        Assert.Equal(Path.GetFullPath(@"C:\proj\src\a.cs"), ws.Resolve(@"src\a.cs"));
    }

    [Fact]
    public void Resolve_Dot_ReturnsRoot()
    {
        var ws = new Workspace(@"C:\proj");
        Assert.Equal(Path.GetFullPath(@"C:\proj"), ws.Resolve("."));
    }

    [Fact]
    public void Resolve_Escape_Throws()
    {
        var ws = new Workspace(@"C:\proj");
        Assert.Throws<ToolException>(() => ws.Resolve(@"..\secret.txt"));
    }

    [Fact]
    public void Resolve_SiblingPrefix_Rejected()
    {
        // C:\proj2 不应被 C:\proj 的沙箱放行
        var ws = new Workspace(@"C:\proj");
        Assert.Throws<ToolException>(() => ws.Resolve(@"..\proj2\x.txt"));
    }

    [Fact]
    public void ToRelative_ConvertsBack()
    {
        var ws = new Workspace(@"C:\proj");
        Assert.Equal(@"src\a.cs", ws.ToRelative(Path.GetFullPath(@"C:\proj\src\a.cs")));
    }
}
