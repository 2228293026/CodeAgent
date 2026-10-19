using System;
using System.IO;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// readline/Emacs 的四个编辑键：<c>Ctrl+A</c>（行首）、<c>Ctrl+E</c>（行尾）、
/// <c>Ctrl+K</c>（删到行尾）、<c>Ctrl+W</c>（删前一个词）。
///
/// 缓冲区（<see cref="EditableLine"/>）早就有 <c>LineHome</c>/<c>LineEnd</c>/<c>DeleteWordBackward</c>，
/// 缺的只是键位映射——而缺键位的代价是**手指要离开主键区**去够 Home/End，
/// 恰恰在提示符行最长的时最难受。
///
/// <c>DeleteToLineEnd</c> 是这轮新加的，语义上有两个坑必须钉住：
/// · 不越过换行——多行输入里一次 Ctrl+K 把两行合成一行，比不做更意外；
/// · 不劈开代理对——emoji 被切成半个高代理是渲染层最难查的错。
/// </summary>
public class ReadlineEditKeysTests
{
    [Fact]
    public void CtrlKDeletesToEndOfLine()
    {
        var line = new EditableLine();
        line.Replace("hello world");
        line.Home();
        line.MoveRight();
        line.MoveRight();
        line.MoveRight();
        line.MoveRight();
        line.MoveRight(); // 光标在 "hello| world"
        Assert.True(line.DeleteToLineEnd());
        Assert.Equal("hello", line.Text);
        Assert.Equal("hello".Length, line.Cursor);
    }

    [Fact]
    public void CtrlKStopsAtTheNewline()
    {
        // 多行输入：只删本行，下一行必须原样留着
        var line = new EditableLine();
        line.Replace("first\nsecond");
        line.Home();
        line.MoveRight(); // "f|irst\nsecond"
        Assert.True(line.DeleteToLineEnd());
        Assert.Equal("f\nsecond", line.Text);
    }

    [Fact]
    public void CtrlKAtLineEndDoesNothing()
    {
        // 行尾无动作：不做「删除整行」那种附带效果
        var line = new EditableLine();
        line.Replace("abc");
        line.End();
        Assert.False(line.DeleteToLineEnd());
        Assert.Equal("abc", line.Text);
    }

    [Fact]
    public void CtrlKOnEmptyLineDoesNothing()
    {
        var line = new EditableLine();
        line.Replace("");
        Assert.False(line.DeleteToLineEnd());
        Assert.Equal(string.Empty, line.Text);
    }

    [Fact]
    public void CtrlKNeverSplitsASurrogatePair()
    {
        // 「截断点落在低代理上」时整对一起删：留半个高代理会让渲染层输出替换字符
        var line = new EditableLine();
        line.Replace("a🎯b");
        line.Home();
        line.MoveRight(); // "a|🎯b"：删到行尾会把 emoji 整对带走
        Assert.True(line.DeleteToLineEnd());
        Assert.Equal("a", line.Text);
        // 剩下的串里不能有孤立代理
        foreach (var ch in line.Text)
            Assert.False(char.IsSurrogate(ch), "留下了一个孤立代理");
    }

    [Fact]
    public void CtrlKFromStartClearsTheWholeLine()
    {
        var line = new EditableLine();
        line.Replace("全部删掉");
        line.Home();
        Assert.True(line.DeleteToLineEnd());
        Assert.Equal(string.Empty, line.Text);
    }

    [Fact]
    public void CtrlKOnSecondLineOnlyTouchesThatLine()
    {
        var line = new EditableLine();
        line.Replace("keep\ndrop this\nkeep too");
        // 光标移到第二行行首
        line.Home();
        for (var i = 0; i < 5; i++) line.MoveRight(); // 跨过 "keep\n"
        Assert.True(line.DeleteToLineEnd());
        Assert.Equal("keep\n\nkeep too", line.Text);
    }

    [Fact]
    public void TheFourKeysAreActuallyWiredIntoTheInputLoop()
    {
        // 缓冲区测得再全，按键没接进 ReadInner 也是白搭——那才是用户真正按到的地方。
        // 四个键必须**各自**出现在 switch 里，且不能只出现在别处的注释中。
        var src = ReadSource("InputLine.cs");
        foreach (var key in new[] { "ConsoleKey.A", "ConsoleKey.E", "ConsoleKey.K", "ConsoleKey.W" })
        {
            var at = src.IndexOf("case " + key + " when (key.Modifiers & ConsoleModifiers.Control) != 0", StringComparison.Ordinal);
            Assert.True(at > 0, $"{key} 没有接进输入循环");
        }
        // Ctrl+W 必须与 Ctrl+Backspace 走同一个方法：同一件事两条路给出不同结果最难查
        Assert.Contains("buf.DeleteWordBackward()", src);
        Assert.Contains("buf.DeleteToLineEnd()", src);
    }

    [Fact]
    public void AppliesToMultiLineInputToo()
    {
        // Ctrl+A/E 走 LineHome/LineEnd（而不是 Home/End 全文首尾）：
        // 多行输入里跳全文首尾几乎总不是用户想要的
        var line = new EditableLine();
        line.Replace("one\ntwo");
        line.End();
        line.LineHome();
        Assert.Equal("one\n".Length, line.Cursor);
        line.LineEnd();
        Assert.Equal("one\ntwo".Length, line.Cursor);
    }

    private static string ReadSource(string file)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent", file);
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 1000)
                    return File.ReadAllText(candidate);
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException($"找不到 src/CodeAgent/{file}");
    }
}
