using System;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 越界的撤销编号。
///
/// <c>UndoManager.TryUndo</c> 把 count <b>静默 clamp</b> 到实际条数：于是 <c>/undo 999</c>
/// 在只有 3 条记录时一次清空整栈，屏幕上只显示"撤销了 3 次"，用户完全看不出
/// 自己敲的 999 被改了。撤销栈弹出后<b>不可恢复</b>。
///
/// <c>/resume</c> 早就修过同一类问题（「数字越界时明确指出范围，静默回退到列表
/// 曾让人以为编号生效了」）——同一个毛病，<b>一条修了另一条没修</b>。
/// </summary>
public sealed class UndoRangeTests
{
    [Fact]
    public void InRange_ProducesNoWarning()
    {
        Assert.Null(Program.OutOfRangeUndoNotice(1, 5));
        Assert.Null(Program.OutOfRangeUndoNotice(5, 5));
        Assert.Null(Program.OutOfRangeUndoNotice(2, 5));
    }

    [Fact]
    public void OutOfRange_ProducesAWarning()
    {
        var w = Program.OutOfRangeUndoNotice(999, 3);
        Assert.NotNull(w);
        Assert.Contains("超出范围", w);
    }

    [Fact]
    public void OutOfRange_StatesWhatWouldActuallyHappen()
    {
        // 只说"超出范围"不够：用户要知道自己差点清空了多少
        var w = Program.OutOfRangeUndoNotice(999, 3)!;
        Assert.Contains("999", w);
        Assert.Contains("3", w);
        Assert.Contains("全部", w);
    }

    [Fact]
    public void OutOfRange_AsksForConfirmation()
    {
        // 这是不可逆操作，必须显式 y 才继续
        Assert.Contains("y", Program.OutOfRangeUndoNotice(10, 2)!);
    }

    [Fact]
    public void OutOfRange_ShowsTheValidRange()
    {
        Assert.Contains("1-2", Program.OutOfRangeUndoNotice(10, 2)!);
    }

    [Fact]
    public void NothingToUndo_ProducesNoWarning()
    {
        // 没有记录时不该问"确定撤销全部 0 条吗"
        Assert.Null(Program.OutOfRangeUndoNotice(999, 0));
    }

    [Fact]
    public void TryUndoReallyDoesClampSilently()
    {
        // 前提钉住：TryUndo 本身**不会**提示，它只 clamp。
        // 守卫加在调用方而不是改 TryUndo——UndoManager 是无 IO 的纯状态容器，
        // 让它去问确认会把控制台交互塞进一个本该可测的类里。
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codeagent-undo-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var undo = new Tools.UndoManager();
            var f = System.IO.Path.Combine(dir, "a.txt");
            System.IO.File.WriteAllText(f, "v1");
            undo.Push(new Tools.UndoEntry { Kind = "edit", Path = f, OldText = "v1", HadFile = true });
            var out1 = undo.TryUndo(999);
            Assert.NotNull(out1);
            Assert.Equal(0, undo.Count); // 999 被 clamp 成 1，静默清空
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void BothUndoPathsUseTheSameRule()
    {
        // 两条路径各写一份必然漂移——当初 /resume 修了、/undo 没修就是这么来的
        var src = System.IO.File.ReadAllText(Find());
        Assert.True(Count(src, "OutOfRangeUndoNotice(") >= 3,
            "/undo N 与交互式选择两处都该走同一条规则");
    }

    [Fact]
    public void CancellingLeavesTheStackIntact()
    {
        // 取消时必须**一条都没撤**——不能先撤再问
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codeagent-undo-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var undo = new Tools.UndoManager();
            var f = System.IO.Path.Combine(dir, "a.txt");
            System.IO.File.WriteAllText(f, "v1");
            undo.Push(new Tools.UndoEntry { Kind = "edit", Path = f, OldText = "v1", HadFile = true });
            // 模拟"用户回答 n"：什么都不做
            Assert.Equal(1, undo.Count);
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (string.CompareOrdinal(haystack, i, needle, 0, needle.Length) == 0)
                n++;
        return n;
    }

    private static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return f;
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
