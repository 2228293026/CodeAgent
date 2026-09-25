using System;
using System.IO;
using System.Linq;
using CodeAgent;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// /undo 列表渲染（最后一个手写对齐的编号列表）。
/// 回归点：曾直接拼 `  {n}) {描述}（{相对时间}）`——编号列不对齐，
/// 描述里的长路径在窄终端硬折行，续行顶到行首与首行失去关联。
/// </summary>
public class UndoListRenderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-undorender-" + Guid.NewGuid().ToString("N"));

    public UndoListRenderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private UndoManager NewManager(int count)
    {
        var um = new UndoManager();
        for (var i = 1; i <= count; i++)
            um.Push(new UndoEntry { Kind = "write", Path = Path.Combine(_dir, $"file{i}.txt"), HadFile = false });
        return um;
    }

    [Fact]
    public void ListEntries_EmptyReturnsEmptyString()
    {
        Assert.Equal(string.Empty, new UndoManager().ListEntries());
    }

    [Fact]
    public void EntryRecords_NumbersFromMostRecent()
    {
        var entries = NewManager(3).EntryRecords(out var hidden);
        Assert.Equal(0, hidden);
        Assert.Equal(3, entries.Count);
        Assert.Equal("1)", entries[0].Command);
        Assert.Equal("3)", entries[2].Command);
        // 最近的排在最前
        Assert.Contains("file3.txt", entries[0].Description, StringComparison.Ordinal);
    }

    [Fact]
    public void EntryRecords_HiddenCountWhenOverMax()
    {
        var entries = NewManager(12).EntryRecords(out var hidden, 10);
        Assert.Equal(2, hidden);
        Assert.Equal(10, entries.Count);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(140)]
    public void ListEntries_NeverExceedsWidth(int width)
    {
        var list = NewManager(8).ListEntries(width: width);
        foreach (var row in list.Replace("\r\n", "\n").Split('\n'))
            Assert.True(TextUtil.DisplayWidth(row) <= width, $"宽度 {width} 溢出: {row}");
    }

    [Fact]
    public void ListEntries_ShowsHiddenCountPrefix()
    {
        var list = NewManager(12).ListEntries(max: 10, width: 80);
        Assert.StartsWith("…（更早 2 条未显示）", list);
    }

    [Fact]
    public void ListEntries_LongPathStaysWithinWidth()
    {
        var um = new UndoManager();
        um.Push(new UndoEntry
        {
            Kind = "write",
            Path = Path.Combine(_dir, "very", "deeply", "nested", "directory", "structure", "SomeFile.cs"),
            HadFile = false,
        });
        var list = um.ListEntries(width: 40);
        foreach (var row in list.Replace("\r\n", "\n").Split('\n'))
            Assert.True(TextUtil.DisplayWidth(row) <= 40, $"溢出: {row}");
        Assert.Contains("1)", list);
    }

    [Fact]
    public void ListEntries_UnknownWidthKeepsEveryEntry()
    {
        var list = NewManager(5).ListEntries();
        Assert.Equal(5, list.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }
}
