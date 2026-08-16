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

    [Fact]
    public void LastDiff_EmptyStack_ReturnsNull()
    {
        var um = new UndoManager();
        Assert.Null(um.LastDiff());
    }

    [Fact]
    public void TryUndo_EditMissingFile_ReportsFailure()
    {
        // edit 撤销时文件已不存在：应报告失败而非崩溃
        var path = Path.Combine(_dir, "gone.txt");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "edit", Path = path, OldText = "old", NewText = "new" });

        var desc = um.TryUndo();
        Assert.NotNull(desc);
        Assert.Contains("文件已不存在", desc);
    }

    [Fact]
    public void TryUndo_UndoAfterLastDiff_KeepsStackConsistent()
    {
        // LastDiff 不应弹出条目；随后 TryUndo 仍应能撤销
        var path = Path.Combine(_dir, "f.txt");
        File.WriteAllText(path, "old");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "write", Path = path, OldText = "old", HadFile = true });
        File.WriteAllText(path, "new");

        Assert.NotNull(um.LastDiff());
        Assert.Equal(1, um.Count); // LastDiff 只读
        Assert.NotNull(um.TryUndo()); // 仍可撤销
        Assert.Equal(0, um.Count);
    }

    [Fact]
    public void TryUndo_EditLargeFileFallback_UndoAfterUndo_IsNoOp()
    {
        // 连续两次撤销：第二次应返回 null（栈已空）
        var path = Path.Combine(_dir, "g.txt");
        File.WriteAllText(path, "old");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "write", Path = path, OldText = "old", HadFile = true });
        File.WriteAllText(path, "new");

        Assert.NotNull(um.TryUndo());
        Assert.Null(um.TryUndo());
    }

    [Theory]
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("unknown-kind")]
    public void Push_AnyKind_DoesNotCrash(string kind)
    {
        // 各种 Kind 的条目入栈都应安全；未知 kind 撤销时按无操作处理（不崩溃）
        var path = Path.Combine(_dir, "h.txt");
        File.WriteAllText(path, "x");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = kind, Path = path, OldText = "x", HadFile = true });

        var desc = um.TryUndo();
        Assert.NotNull(desc); // 至少返回描述文本，不崩溃
    }

    [Fact]
    public void TryUndo_EditLargeFileFallback_PairNotFound_KeepsFile()
    {
        // 大文件退化：文件中找不到 NewText 时 Replace 无效果，文件保持不变（不崩溃、不报错）
        var path = Path.Combine(_dir, "i.txt");
        File.WriteAllText(path, "hello world");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "edit", Path = path, OldText = "zzz", NewText = "qqq" });

        var desc = um.TryUndo();
        Assert.NotNull(desc);
        Assert.Equal("hello world", File.ReadAllText(path)); // 无匹配 → 文件原样
    }
}
