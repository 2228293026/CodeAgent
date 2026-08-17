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

    // ===== /undo N 多条撤销与 ListEntries =====

    [Fact]
    public void TryUndo_Multiple_UndoesInReverseOrder()
    {
        var a = Path.Combine(_dir, "a2.txt");
        var b = Path.Combine(_dir, "b2.txt");
        File.WriteAllText(a, "old-a");
        File.WriteAllText(b, "old-b");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "write", Path = a, OldText = "old-a", HadFile = true });
        um.Push(new UndoEntry { Kind = "write", Path = b, OldText = "old-b", HadFile = true });
        File.WriteAllText(a, "new-a");
        File.WriteAllText(b, "new-b");

        var desc = um.TryUndo(2);
        Assert.NotNull(desc);
        Assert.Contains("b2.txt", desc); // 先撤销最近（b）
        Assert.Contains("a2.txt", desc); // 再撤销 a
        Assert.Equal("old-a", File.ReadAllText(a));
        Assert.Equal("old-b", File.ReadAllText(b));
        Assert.Equal(0, um.Count);
    }

    [Fact]
    public void TryUndo_CountLargerThanStack_Clamps()
    {
        var path = Path.Combine(_dir, "c2.txt");
        File.WriteAllText(path, "old");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "write", Path = path, OldText = "old", HadFile = true });
        File.WriteAllText(path, "new");

        var desc = um.TryUndo(100); // 超出栈长度 → 只撤销栈内条目，不崩溃
        Assert.NotNull(desc);
        Assert.Equal("old", File.ReadAllText(path));
        Assert.Equal(0, um.Count);
    }

    [Fact]
    public void ListEntries_ListsRecentFirst()
    {
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "write", Path = Path.Combine(_dir, "x1.txt"), HadFile = false });
        um.Push(new UndoEntry { Kind = "write", Path = Path.Combine(_dir, "x2.txt"), HadFile = false });

        var list = um.ListEntries();
        Assert.Contains("1)", list);
        Assert.Contains("2)", list);
        // 最近的在最前（编号 1）：x2 应出现在 x1 之前
        Assert.True(list.IndexOf("x2.txt", StringComparison.Ordinal) < list.IndexOf("x1.txt", StringComparison.Ordinal));
        Assert.Equal(2, um.Count); // ListEntries 只读
    }

    // ===== 命令副作用（cmd）条目 =====

    [Fact]
    public void CmdEntry_ModifiedFile_RestoresOldContent()
    {
        // bash 修改了文件：HadFile=true + OldText=执行前内容，撤销写回旧内容
        var path = Path.Combine(_dir, "m.txt");
        File.WriteAllText(path, "before-cmd");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "cmd", Path = path, OldText = "before-cmd", HadFile = true });
        File.WriteAllText(path, "after-cmd");

        var desc = um.TryUndo();
        Assert.NotNull(desc);
        Assert.Contains("命令副作用", desc);
        Assert.Equal("before-cmd", File.ReadAllText(path));
    }

    [Fact]
    public void CmdEntry_NewFile_Deletes()
    {
        // bash 新建了文件：HadFile=false，撤销删除
        var path = Path.Combine(_dir, "created.txt");
        File.WriteAllText(path, "created-by-cmd");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "cmd", Path = path, OldText = null, HadFile = false });

        var desc = um.TryUndo();
        Assert.NotNull(desc);
        Assert.Contains("删除新建文件", desc);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CmdEntry_DeletedFile_Rebuilds()
    {
        // bash 删除了文件：HadFile=true + OldText=执行前内容（当前文件不存在），撤销重建
        var path = Path.Combine(_dir, "deleted.txt");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "cmd", Path = path, OldText = "content-before", HadFile = true });
        Assert.False(File.Exists(path));

        var desc = um.TryUndo();
        Assert.NotNull(desc);
        Assert.Contains("重建被删文件", desc);
        Assert.Equal("content-before", File.ReadAllText(path));
    }

    // ===== SnapshotDir / RecordCommandSideEffects =====

    [Fact]
    public void SnapshotDir_RecordsTextFiles_SkipsBigAndBinaries()
    {
        File.WriteAllText(Path.Combine(_dir, "small.txt"), "hello");
        using (var fs = File.Create(Path.Combine(_dir, "big.bin")))
        {
            fs.SetLength(2 * 1024 * 1024); // >1MB：不记录
        }
        Directory.CreateDirectory(Path.Combine(_dir, "bin")); // 跳过目录
        File.WriteAllText(Path.Combine(_dir, "bin", "build.dll.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        File.WriteAllText(Path.Combine(_dir, ".git", "config"), "y");

        var snap = UndoManager.SnapshotDir(_dir);
        Assert.Equal("hello", snap["small.txt"]);
        Assert.False(snap.ContainsKey("big.bin"));
        Assert.False(snap.ContainsKey("bin/build.dll.txt")); // 构建目录被剪枝
        Assert.False(snap.ContainsKey(".git/config"));       // 版本控制目录被剪枝
    }

    [Fact]
    public void RecordCommandSideEffects_DetectsCreateModifyDelete()
    {
        var dir = Path.Combine(_dir, "work");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mod.txt"), "old");
        File.WriteAllText(Path.Combine(dir, "gone.txt"), "will-be-deleted");

        var before = UndoManager.SnapshotDir(dir);
        // 模拟命令副作用：修改 mod.txt、删除 gone.txt、新建 new.txt
        File.WriteAllText(Path.Combine(dir, "mod.txt"), "new");
        File.Delete(Path.Combine(dir, "gone.txt"));
        File.WriteAllText(Path.Combine(dir, "new.txt"), "created");

        var um = new UndoManager();
        UndoManager.RecordCommandSideEffects(dir, before, um);

        Assert.Equal(3, um.Count); // mod/gone/new 各一条
        var undos = um.ListEntries();
        Assert.Contains("mod.txt", undos);
        Assert.Contains("gone.txt", undos);
        Assert.Contains("new.txt", undos);

        // 全部撤销后恢复执行前状态
        um.TryUndo(3);
        Assert.Equal("old", File.ReadAllText(Path.Combine(dir, "mod.txt")));
        Assert.True(File.Exists(Path.Combine(dir, "gone.txt")));
        Assert.False(File.Exists(Path.Combine(dir, "new.txt")));
    }

    // ===== AllDiffs（/diff 展示全部改动，含新建文件）=====

    [Fact]
    public void AllDiffs_NewFile_ShowsAddedLines()
    {
        // 新建文件：/diff 应展示全新增内容（回归：之前只显示栈顶条目且空原文输出异常）
        var path = Path.Combine(_dir, "created.txt");
        File.WriteAllText(path, "line1\nline2\n");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "write", Path = path, HadFile = false });

        var all = um.AllDiffs();
        Assert.NotNull(all);
        Assert.Contains("created.txt", all);
        Assert.Contains("+ line1", all);
        Assert.Contains("+ line2", all);
    }

    [Fact]
    public void AllDiffs_MultipleFiles_ShowsAllEntries()
    {
        // 多个待撤销改动：/diff 应展示每个文件（含新建与修改），而非只显示最近一个
        var created = Path.Combine(_dir, "n1.txt");
        var modified = Path.Combine(_dir, "n2.txt");
        File.WriteAllText(created, "new content");
        File.WriteAllText(modified, "old content");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "write", Path = created, HadFile = false });
        um.Push(new UndoEntry { Kind = "write", Path = modified, OldText = "old content", HadFile = true });
        File.WriteAllText(modified, "new content");

        var all = um.AllDiffs();
        Assert.NotNull(all);
        Assert.Contains("n1.txt", all);   // 新建文件
        Assert.Contains("n2.txt", all);   // 修改文件
        Assert.Contains("+ new content", all);
        Assert.Contains("- old content", all);
        Assert.Equal(2, um.Count); // AllDiffs 只读
    }

    [Fact]
    public void AllDiffs_EmptyStack_ReturnsNull()
    {
        var um = new UndoManager();
        Assert.Null(um.AllDiffs());
    }
}
