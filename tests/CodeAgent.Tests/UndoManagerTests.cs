using System;
using System.IO;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public class UndoManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-tests-" + Guid.NewGuid().ToString("N"));

    public UndoManagerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    [Fact]
    public void TryUndo_RestoresOverwrittenFile()
    {
        var path = Path.Combine(_dir, "a.txt");
        File.WriteAllText(path, "old");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "write", Path = path, OldText = "old", HadFile = true });
        File.WriteAllText(path, "new");

        var desc = um.TryUndo();
        Assert.NotNull(desc);
        Assert.Equal("old", File.ReadAllText(path));
        Assert.Equal(0, um.Count);
    }

    [Fact]
    public void TryUndo_DeletesNewlyCreatedFile()
    {
        var path = Path.Combine(_dir, "b.txt");
        File.WriteAllText(path, "content");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "write", Path = path, HadFile = false });

        var desc = um.TryUndo();
        Assert.NotNull(desc);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryUndo_EmptyStack_ReturnsNull()
    {
        var um = new UndoManager();
        Assert.Null(um.TryUndo());
    }

    [Fact]
    public void Push_CapsAtFiftyEntries()
    {
        var um = new UndoManager();
        for (int i = 0; i < 60; i++)
            um.Push(new UndoEntry { Kind = "write", Path = Path.Combine(_dir, "x"), HadFile = false });
        Assert.Equal(50, um.Count);
    }

    [Fact]
    public void LastDiff_ShowsChange()
    {
        var path = Path.Combine(_dir, "c.txt");
        File.WriteAllText(path, "hello");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "write", Path = path, OldText = "hello", HadFile = true });
        File.WriteAllText(path, "world");

        var diff = um.LastDiff();
        Assert.NotNull(diff);
        Assert.Contains("- hello", diff);
        Assert.Contains("+ world", diff);
    }
}
