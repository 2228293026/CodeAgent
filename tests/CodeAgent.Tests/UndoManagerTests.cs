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
    public void TryUndo_LargeFileWithoutSnapshot_ReportsHonestly()
    {
        // 回归：>4MB 文件覆盖时未记录原内容（OldText=null），撤销应如实说明而非谎报成功
        var path = Path.Combine(_dir, "big.txt");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "write", Path = path, OldText = null, HadFile = true });
        File.WriteAllText(path, "new content");

        var desc = um.TryUndo();
        Assert.NotNull(desc);
        Assert.Contains("无法撤销", desc);
        Assert.Equal("new content", File.ReadAllText(path)); // 文件未被改动
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

    [Fact]
    public void TryUndo_EditWithFullSnapshot_RestoresExactly()
    {
        // 回归：旧实现用 text.Replace(new, old) 撤销，当新文本在文件中多处存在时会破坏内容。
        // 例如 replace_all 把 "bar" 全替换为 "foo" 后，撤销会把所有 "foo" 都换回 "bar"。
        var path = Path.Combine(_dir, "d.txt");
        File.WriteAllText(path, "foo bar foo");
        var um = new UndoManager();
        // 模拟 EditFileTool：小文件记录完整原文（NewText = null）
        um.Push(new UndoEntry { Kind = "edit", Path = path, OldText = "foo bar foo", NewText = null });
        File.WriteAllText(path, "foo foo foo");

        var desc = um.TryUndo();
        Assert.NotNull(desc);
        Assert.Equal("foo bar foo", File.ReadAllText(path));
    }

    [Fact]
    public void TryUndo_EditLargeFileFallback_ReplacesPair()
    {
        // 大文件（>4MB）不记录完整原文，退化为 old/new 对：撤销时把新文本替换回旧文本
        var path = Path.Combine(_dir, "big.txt");
        File.WriteAllText(path, "hello old tail");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "edit", Path = path, OldText = "old", NewText = "new" });
        File.WriteAllText(path, "hello new tail");

        var desc = um.TryUndo();
        Assert.NotNull(desc);
        Assert.Equal("hello old tail", File.ReadAllText(path));
    }

    [Fact]
    public void LastDiff_EditWithFullSnapshot_ShowsExactOriginal()
    {
        var path = Path.Combine(_dir, "e.txt");
        File.WriteAllText(path, "foo bar foo");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "edit", Path = path, OldText = "foo bar foo", NewText = null });
        File.WriteAllText(path, "foo foo foo");

        var diff = um.LastDiff();
        Assert.NotNull(diff);
        Assert.Contains("- foo bar foo", diff); // - 为原始内容
        Assert.Contains("+ foo foo foo", diff); // + 为当前内容
    }
}
