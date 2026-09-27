using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 输入历史的单条长度上限。
///
/// 粘贴一大段代码会进来几万字符，此前**原样入库**：当次会话里 ↑ 一次就把它全量灌回输入框
/// （重绘与折叠都要处理它），下次启动还会从文件里整个读回来。
/// 而 <c>Save()</c> 每次提交都全量重写文件——100 条大条目就是几 MB 的写放大。
/// </summary>
public sealed class HistoryEntryCapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-hist-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public HistoryEntryCapTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "history.txt");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // —— 规则本身 ——

    [Fact]
    public void Cap_RejectsEmptyAndWhitespace()
    {
        Assert.False(HistoryStore.ShouldRemember(""));
        Assert.False(HistoryStore.ShouldRemember("   "));
        Assert.False(HistoryStore.ShouldRemember("\n\t "));
    }

    [Fact]
    public void Cap_RejectsOversized()
    {
        Assert.False(HistoryStore.ShouldRemember(new string('x', HistoryStore.MaxEntryChars + 1)));
        Assert.False(HistoryStore.ShouldRemember(new string('x', 100_000)));
    }

    [Fact]
    public void Cap_AcceptsAtTheBoundary()
    {
        Assert.True(HistoryStore.ShouldRemember(new string('x', HistoryStore.MaxEntryChars)));
    }

    [Fact]
    public void Cap_AcceptsOrdinaryInput()
    {
        Assert.True(HistoryStore.ShouldRemember("ls -la"));
        Assert.True(HistoryStore.ShouldRemember("/help"));
        Assert.True(HistoryStore.ShouldRemember("echo " + new string('a', 500)));
    }

    [Fact]
    public void Cap_CountsNewlinesAsCharacters()
    {
        // 多行粘贴按字符数算，含换行——1000 行的块就是 1000+ 字符
        var block = string.Join("\n", Enumerable.Repeat("some code line", 300));
        Assert.True(block.Length > HistoryStore.MaxEntryChars);
        Assert.False(HistoryStore.ShouldRemember(block));
    }

    // —— 落地行为 ——

    [Fact]
    public void OversizedEntry_IsNotStored()
    {
        var h = new HistoryStore(_file);
        h.Remember("/help");
        h.Remember(new string('x', 50_000));
        Assert.Equal(1, h.Count);
        Assert.Contains("/help", h.Entries);
    }

    [Fact]
    public void OversizedEntry_LeavesNoFileAtAll()
    {
        var h = new HistoryStore(_file);
        for (var i = 0; i < 20; i++)
            h.Remember(string.Join("\n", Enumerable.Repeat("x", 5000)));
        Assert.Equal(0, h.Count);
        // 全部被跳过 → Save() 从未被调用 → 磁盘上连文件都没有。
        // 这比"文件很小"更强：既没有写放大，也没有留下空壳文件
        Assert.False(File.Exists(_file), "全是超长条目时不该留下任何历史文件");
    }

    [Fact]
    public void OversizedEntry_AlreadyInFileIsSkippedOnLoad()
    {
        // 只在写入侧加规则的话，改规则之前写进去的老文件仍会在启动时
        // 把几万字符灌进输入框——那正是这条规则要防的场景
        File.WriteAllText(_file, "/help\n" + new string('x', 80_000) + "\nls -la\n");
        var h = new HistoryStore(_file);
        Assert.Equal(2, h.Count);
        Assert.Contains("/help", h.Entries);
        Assert.Contains("ls -la", h.Entries);
        Assert.DoesNotContain(h.Entries, e => e.Length > HistoryStore.MaxEntryChars);
    }

    [Fact]
    public void OrdinaryEntries_RoundTripThroughTheFile()
    {
        var h = new HistoryStore(_file);
        h.Remember("第一条");
        h.Remember("second line\nwith newline");
        h.Remember("with backslash C:\\path\\to");
        var reloaded = new HistoryStore(_file);
        Assert.Equal(3, reloaded.Count);
        Assert.Contains("第一条", reloaded.Entries);
        Assert.Contains("second line\nwith newline", reloaded.Entries);
        Assert.Contains("with backslash C:\\path\\to", reloaded.Entries);
    }

    [Fact]
    public void Cap_IsNotAppliedToRemoveOrClear()
    {
        // 已在库里的老条目仍应能被 /history 之类移除
        var h = new HistoryStore(_file);
        h.Remember("/help");
        Assert.True(h.Remove("/HELP"));
        Assert.Equal(0, h.Count);
    }

    [Fact]
    public void Cap_DuplicateHandlingStillWorks()
    {
        var h = new HistoryStore(_file);
        h.Remember("cmd");
        h.Remember("other");
        h.Remember("cmd");
        Assert.Equal(2, h.Count);
        Assert.Equal("cmd", h.Entries[^1]);
    }
}
