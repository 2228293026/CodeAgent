using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>EditableLine 与 HistoryStore 的边界测试（补充 EditableLineTests / HistoryStoreTests 未覆盖的场景）。</summary>
public class EditableHistoryEdgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-edit-" + Guid.NewGuid().ToString("N"));

    public EditableHistoryEdgeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    // ===== EditableLine 边界 =====

    [Fact]
    public void Insert_IntoEmptyLine()
    {
        var l = new EditableLine();
        l.Insert('a');
        Assert.Equal("a", l.Text);
        Assert.Equal(1, l.Cursor);
    }

    [Fact]
    public void Insert_AtStart_AfterHome()
    {
        var l = new EditableLine();
        l.SetInitial("bc");
        l.Home();
        l.Insert('a');
        Assert.Equal("abc", l.Text);
        Assert.Equal(1, l.Cursor);
    }

    [Fact]
    public void Insert_ChineseChar_AdvancesByOne()
    {
        var l = new EditableLine();
        l.SetInitial("ab");
        l.Insert('中');
        Assert.Equal("ab中", l.Text);
        Assert.Equal(3, l.Cursor); // char 计数（显示宽度另由 CursorLeftOffset 处理）
    }

    [Fact]
    public void Backspace_EmptyLine_NoOp()
    {
        var l = new EditableLine();
        Assert.False(l.Backspace());
        Assert.Equal("", l.Text);
        Assert.Equal(0, l.Cursor);
    }

    [Fact]
    public void Delete_EmptyLine_NoOp()
    {
        var l = new EditableLine();
        Assert.False(l.Delete());
        Assert.Equal(0, l.Cursor);
    }

    [Fact]
    public void Backspace_ThenInsert_BehavesLikeEditor()
    {
        var l = new EditableLine();
        l.SetInitial("abc"); // 光标 3
        l.MoveLeft();        // 光标 2（'b' 后）
        l.Backspace();       // 删除 'b' → "ac"，光标 1
        l.Insert('x');       // 光标 1 插入 → "axc"
        Assert.Equal("axc", l.Text);
        Assert.Equal(2, l.Cursor);
    }

    [Fact]
    public void Replace_EmptyText_ClearsAndResetsCursor()
    {
        var l = new EditableLine();
        l.SetInitial("abc");
        l.Replace("");
        Assert.Equal("", l.Text);
        Assert.Equal(0, l.Cursor);
    }

    [Fact]
    public void SetInitial_NonEmpty_MovesCursorToEnd()
    {
        var l = new EditableLine();
        l.SetInitial("hello");
        Assert.Equal(5, l.Cursor);
    }

    [Fact]
    public void SetInitial_Null_IsNoOp()
    {
        var l = new EditableLine();
        l.SetInitial(null);
        Assert.Equal("", l.Text);
        Assert.Equal(0, l.Cursor);
    }

    [Fact]
    public void Clear_ResetsCursorToZero()
    {
        var l = new EditableLine();
        l.SetInitial("abc");
        l.MoveLeft();
        l.Clear();
        Assert.Equal("", l.Text);
        Assert.Equal(0, l.Cursor);
    }

    [Fact]
    public void Cursor_StaysWithinBounds_AfterMixedOps()
    {
        // 随机顺序操作后光标始终在 [0, Length] 内
        var l = new EditableLine();
        l.SetInitial("abcdef");
        l.MoveLeft(); l.MoveLeft(); l.Backspace(); l.Insert('z');
        l.Home(); l.Delete(); l.End(); l.MoveRight(); l.Backspace();
        Assert.InRange(l.Cursor, 0, l.Text.Length); // 混合操作后光标仍在 [0, Length]
    }

    [Fact]
    public void MoveRight_AtEnd_StaysPut()
    {
        var l = new EditableLine();
        l.SetInitial("ab");
        l.End();
        l.MoveRight();
        Assert.Equal(2, l.Cursor);
    }

    [Fact]
    public void MoveLeft_AtStart_StaysPut()
    {
        var l = new EditableLine();
        l.SetInitial("ab");
        l.Home();
        l.MoveLeft();
        Assert.Equal(0, l.Cursor);
    }

    [Fact]
    public void Insert_AtMiddle_PushesRemainder()
    {
        var l = new EditableLine();
        l.SetInitial("ac");
        l.MoveLeft(); // 光标在 'c' 前
        l.Insert('b');
        Assert.Equal("abc", l.Text);
        Assert.Equal(2, l.Cursor);
    }

    [Fact]
    public void ToString_MatchesText()
    {
        var l = new EditableLine();
        l.SetInitial("x");
        l.Insert('y');
        Assert.Equal("xy", l.ToString());
    }

    // ===== HistoryStore 边界 =====

    private HistoryStore NewStore() =>
        new(Path.Combine(_dir, ".codeagent", "history-" + Guid.NewGuid().ToString("N") + ".txt"));

    [Fact]
    public void Remember_WhitespaceOnly_IsIgnored()
    {
        var h = NewStore();
        h.Remember("   ");
        h.Remember("\t");
        Assert.Equal(0, h.Count);
    }

    [Fact]
    public void Remember_EmptyString_IsIgnored()
    {
        var h = NewStore();
        h.Remember("");
        Assert.Equal(0, h.Count);
    }

    [Fact]
    public void Remember_DifferentCase_NotDuplicate()
    {
        var h = NewStore();
        h.Remember("/mode");
        h.Remember("/MODE");
        Assert.Equal(2, h.Count); // 大小写敏感：算两条
    }

    [Fact]
    public void Remember_CreatesDirectoryAutomatically()
    {
        var path = Path.Combine(_dir, "deep", "nested", "history.txt");
        var h = new HistoryStore(path);
        h.Remember("a");
        Assert.True(File.Exists(path)); // Save 自动建目录
    }

    [Fact]
    public void Entries_Order_OldToNew()
    {
        var h = NewStore();
        h.Remember("first");
        h.Remember("second");
        Assert.Equal(new[] { "first", "second" }, h.Entries);
    }

    [Fact]
    public void Reload_AfterManyEntries_KeepsLatestCap()
    {
        var path = Path.Combine(_dir, "cap.txt");
        var h = new HistoryStore(path);
        for (int i = 0; i < 250; i++)
            h.Remember($"cmd{i}");
        Assert.Equal(HistoryStore.MaxEntries, h.Count);

        var h2 = new HistoryStore(path); // 重新加载
        Assert.Equal(HistoryStore.MaxEntries, h2.Count);
        Assert.Equal("cmd249", h2.Entries[^1]);
        Assert.Equal("cmd150", h2.Entries[0]); // 最旧被丢弃
    }

    [Fact]
    public void Save_Failure_DoesNotThrow()
    {
        // 写入失败应静默（不影响主流程）。用独占锁占用历史文件触发 IOException：
        // Windows 目录只读属性其实拦不住建文件，锁不住真正的失败路径
        var path = Path.Combine(_dir, "h.txt");
        File.WriteAllText(path, "seed");
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var h = new HistoryStore(path);
        h.Remember("a"); // 不应抛异常
        // 独占锁使构造时的 Load 也失败（seed 未加载，Count 从 0 开始），Save 静默失败但内存条目保留
        Assert.Equal(1, h.Count);
    }

    [Fact]
    public void Load_CorruptLines_AreSkipped()
    {
        // 文件中混入空行/空白行：加载时跳过（ReadAllLines 不会失败，但行为应一致）
        var path = Path.Combine(_dir, "sparse.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, new[] { "a", "", "b", "   ", "c" });
        var h = new HistoryStore(path);
        Assert.Equal(new[] { "a", "b", "c" }, h.Entries);
    }
}
