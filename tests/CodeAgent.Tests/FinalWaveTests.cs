using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeAgent;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>UndoManager / Workspace / HistoryStore / Modes / DiffUtil 的剩余边界用例。</summary>
public class FinalWaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-final-" + Guid.NewGuid().ToString("N"));

    public FinalWaveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    private string PathOf(string rel) => Path.Combine(_dir, rel);

    private string PathOf(string a, string b) => Path.Combine(_dir, a, b);

    // ===== UndoManager =====

    [Fact]
    public void TryUndo_MixedKinds_AllUndone()
    {
        var a = PathOf("a.txt");
        var b = PathOf("b.txt");
        File.WriteAllText(a, "old-a");
        File.WriteAllText(b, "old-b");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "write", Path = a, OldText = "old-a", HadFile = true });
        um.Push(new UndoEntry { Kind = "edit", Path = b, OldText = "old-b", NewText = null });
        File.WriteAllText(a, "new-a");
        File.WriteAllText(b, "new-b");

        um.TryUndo(2);
        Assert.Equal("old-a", File.ReadAllText(a));
        Assert.Equal("old-b", File.ReadAllText(b));
        Assert.Equal(0, um.Count);
    }

    [Fact]
    public void ListEntries_RespectsMax()
    {
        var um = new UndoManager();
        for (int i = 0; i < 20; i++)
            um.Push(new UndoEntry { Kind = "write", Path = PathOf($"f{i}.txt"), HadFile = false });

        var list = um.ListEntries(max: 5);
        Assert.Equal(5, list.Split('\n').Length);
        Assert.DoesNotContain("f14.txt", list); // 只列最近 5 条
        Assert.Contains("f19.txt", list);
    }

    [Fact]
    public void TryUndo_ZeroCount_ActsAsOne()
    {
        var path = PathOf("z.txt");
        File.WriteAllText(path, "old");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "write", Path = path, OldText = "old", HadFile = true });
        File.WriteAllText(path, "new");

        Assert.NotNull(um.TryUndo(0)); // count=0 钳制为 1，撤销 1 条
        Assert.Equal("old", File.ReadAllText(path));
    }

    [Fact]
    public void AllDiffs_IncludesCmdEntries()
    {
        var path = PathOf("cmd.txt");
        File.WriteAllText(path, "after-cmd");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "cmd", Path = path, OldText = "before-cmd", HadFile = true });

        var all = um.AllDiffs();
        Assert.NotNull(all);
        Assert.Contains("cmd.txt", all);
        Assert.Contains("- before-cmd", all);
        Assert.Contains("+ after-cmd", all);
    }

    [Fact]
    public void LastDiff_AfterUndo_FallsToNextEntry()
    {
        var a = PathOf("l1.txt");
        var b = PathOf("l2.txt");
        File.WriteAllText(a, "old-a");
        File.WriteAllText(b, "old-b");
        var um = new UndoManager();
        um.Push(new UndoEntry { Kind = "write", Path = a, OldText = "old-a", HadFile = true });
        um.Push(new UndoEntry { Kind = "write", Path = b, OldText = "old-b", HadFile = true });
        File.WriteAllText(a, "new-a");
        File.WriteAllText(b, "new-b");

        um.TryUndo(); // 撤销 b
        var diff = um.LastDiff();
        Assert.NotNull(diff);
        Assert.Contains("l1.txt", diff); // 剩余栈顶是 a
    }

    [Fact]
    public void AllDiffs_EmptyStack_ReturnsNull()
    {
        Assert.Null(new UndoManager().AllDiffs());
    }

    // ===== Workspace =====

    [Fact]
    public void ResolveRead_WorkspaceInside_EqualsResolve()
    {
        var ws = new Workspace(_dir);
        Assert.Equal(ws.Resolve("a/b.cs"), ws.ResolveRead("a/b.cs"));
    }

    [Fact]
    public void ToRelative_Outside_PrefixedWithDots()
    {
        var ws = new Workspace(_dir);
        var outside = Path.Combine(Path.GetTempPath(), "outside-dir");
        var rel = ws.ToRelative(outside);
        Assert.StartsWith("..", rel); // 工作区外 → ../ 形式
    }

    [Fact]
    public void ToRelative_ChildDir_NoTrailingSep()
    {
        var ws = new Workspace(_dir);
        var sub = Path.Combine(_dir, "src");
        Assert.Equal("src", ws.ToRelative(sub));
    }

    [Fact]
    public void Workspace_RelativeReadOnlyDir_InsideRoot_NotWhitelisted()
    {
        // 相对白名单落在工作区内：本来就是工作区的一部分，白名单无额外意义
        var ws = new Workspace(_dir, new[] { "sub" }, "whitelist");
        Assert.Equal(ws.Resolve("sub/x.txt"), ws.ResolveRead("sub/x.txt"));
        // 相对白名单解析后落在工作区内：不出现在白名单根列表的外部项里
        Assert.All(ws.ReadOnlyRoots, r => r.StartsWith(Path.GetFullPath(_dir), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Workspace_WhitelistFull_ReadWrite()
    {
        var ext = Path.Combine(Path.GetTempPath(), "final-ext");
        var ws = new Workspace(_dir, new[] { ext }, "full");
        var p = Path.Combine(ext, "x.txt");
        Assert.Equal(Path.GetFullPath(p), ws.Resolve(p));   // 写放行
        Assert.Equal(Path.GetFullPath(p), ws.ResolveRead(p));
    }

    [Fact]
    public void Workspace_Strict_RejectsAbsoluteOutside()
    {
        var ws = new Workspace(_dir);
        var outside = Path.Combine(Path.GetTempPath(), "final-abs");
        Assert.Throws<ToolException>(() => ws.Resolve(outside));
        Assert.Throws<ToolException>(() => ws.ResolveRead(outside));
    }

    // ===== HistoryStore =====

    private HistoryStore NewStore() => new(Path.Combine(_dir, "hist-" + Guid.NewGuid().ToString("N") + ".txt"));

    [Fact]
    public void Remember_TwoDistinct_AppendsInOrder()
    {
        var h = NewStore();
        h.Remember("one");
        h.Remember("two");
        Assert.Equal(new[] { "one", "two" }, h.Entries);
    }

    [Fact]
    public void Remember_DuplicateAfterOther_MovesToEnd()
    {
        // 语义变更：非连续重复不再保留旧副本，而是移到末尾（↑/↓ 与 Ctrl+R 不再散落旧副本）
        var h = NewStore();
        h.Remember("a");
        h.Remember("b");
        h.Remember("a");
        Assert.Equal(2, h.Count);
        Assert.Equal(new[] { "b", "a" }, h.Entries);
    }

    [Fact]
    public void Entries_ExposesReadOnlyInterface()
    {
        var h = NewStore();
        h.Remember("x");
        // Entries 只暴露只读接口（IReadOnlyList）：外部无法修改历史
        Assert.IsAssignableFrom<IReadOnlyList<string>>(h.Entries);
    }

    [Fact]
    public void Reload_PreservesOrder()
    {
        var path = Path.Combine(_dir, "order.txt");
        var h = new HistoryStore(path);
        h.Remember("first");
        h.Remember("second");
        h.Remember("third");

        var h2 = new HistoryStore(path);
        Assert.Equal(new[] { "first", "second", "third" }, h2.Entries);
    }

    [Fact]
    public void Load_FileWithOnlyBlankLines_StartsEmpty()
    {
        var path = Path.Combine(_dir, "blank.txt");
        File.WriteAllText(path, "\n\n   \n");
        var h = new HistoryStore(path);
        Assert.Empty(h.Entries);
    }

    // ===== Modes =====

    [Fact]
    public void Find_WhitespaceName_FallsBackToCode()
    {
        Assert.Equal("code", Modes.Find("   ", new AgentConfig()).Name);
    }

    [Fact]
    public void Build_CustomMode_BlankName_IsSkipped()
    {
        var cfg = new AgentConfig { Modes = [new AgentModeConfig { Name = "" }, new AgentModeConfig { Name = "ok" }] };
        var modes = Modes.Build(cfg);
        Assert.DoesNotContain(modes, m => string.IsNullOrWhiteSpace(m.Name));
        Assert.Contains(modes, m => m.Name == "ok");
    }

    [Fact]
    public void Build_CustomMode_BlankPrompt_UsesDefault()
    {
        var cfg = new AgentConfig { Modes = [new AgentModeConfig { Name = "empty-prompt" }] };
        var mode = Modes.Build(cfg).First(m => m.Name == "empty-prompt");
        Assert.Equal(AgentConfig.DefaultSystemPrompt, mode.SystemPrompt);
    }

    [Fact]
    public void Find_CustomMode_ToolsList_Applied()
    {
        var cfg = new AgentConfig { Modes = [new AgentModeConfig { Name = "ro", Tools = ["read_file", "stop"] }] };
        var mode = Modes.Find("ro", cfg);
        Assert.Equal(new[] { "read_file", "stop" }, mode.AllowedTools);
    }

    // ===== DiffUtil =====

    [Fact]
    public void Unified_LargeMiddleChange_ProducesHunk()
    {
        var oldT = string.Join('\n', Enumerable.Range(0, 20).Select(i => $"line{i}"));
        var newT = oldT.Replace("line5", "changed5").Replace("line6", "changed6");
        var d = CodeAgent.DiffUtil.Unified(oldT, newT, "f.txt");
        Assert.Contains("- line5", d);
        Assert.Contains("+ changed5", d);
        Assert.Contains("@@", d);
    }

    [Fact]
    public void Unified_EmptyBoth_ReturnsEmpty()
    {
        Assert.Equal("", CodeAgent.DiffUtil.Unified("", "", "f.txt"));
    }

    [Fact]
    public void Unified_OnlyNewlines_ReturnsEmpty()
    {
        Assert.Equal("", CodeAgent.DiffUtil.Unified("\n\n", "\n\n", "f.txt"));
    }

    [Fact]
    public void Unified_OneLineAdded_ShowsPlus()
    {
        var d = CodeAgent.DiffUtil.Unified("a\n", "a\nb\n", "f.txt");
        Assert.Contains("+ b", d); // 新增行（前有 1 行上下文，hunk 起始行号 2，不纠结具体值）
    }

    // ===== UndoManager 命令副作用快照 =====

    [Fact]
    public void SnapshotDir_SkipsSkippedDirs()
    {
        Directory.CreateDirectory(PathOf("src"));
        File.WriteAllText(PathOf("src", "keep.cs"), "k");
        Directory.CreateDirectory(PathOf("node_modules"));
        File.WriteAllText(PathOf("node_modules", "skip.js"), "s");

        var snap = UndoManager.SnapshotDir(_dir);
        Assert.Contains(snap.Texts, kv => kv.Key.EndsWith("keep.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(snap.Texts, kv => kv.Key.Contains("node_modules", StringComparison.Ordinal));
    }

    [Fact]
    public void SnapshotDir_MissingDir_ReturnsEmpty()
    {
        Assert.Empty(UndoManager.SnapshotDir(PathOf("nope")).Texts);
    }

    [Fact]
    public void RecordCommandSideEffects_NoChanges_PushesNothing()
    {
        var dir = PathOf("stable");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "s.txt"), "same");
        var before = UndoManager.SnapshotDir(dir);

        var um = new UndoManager();
        UndoManager.RecordCommandSideEffects(dir, before, um);
        Assert.Equal(0, um.Count); // 无变更不入栈
    }
}
