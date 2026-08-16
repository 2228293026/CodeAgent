using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class HistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-history-" + Guid.NewGuid().ToString("N"));
    private readonly string _file = Path.Combine(Path.GetTempPath(), "codeagent-history-" + Guid.NewGuid().ToString("N"), "history.txt");

    public HistoryStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    [Fact]
    public void Remember_AppendsEntries()
    {
        var store = new HistoryStore(_file);
        store.Remember("第一条");
        store.Remember("第二条");
        Assert.Equal(2, store.Count);
        Assert.Equal(["第一条", "第二条"], store.Entries);
    }

    [Fact]
    public void Remember_BlankLine_IsIgnored()
    {
        var store = new HistoryStore(_file);
        store.Remember("   ");
        store.Remember("");
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Remember_ConsecutiveDuplicate_IsIgnored()
    {
        var store = new HistoryStore(_file);
        store.Remember("cmd");
        store.Remember("cmd"); // 连续重复
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Remember_NonConsecutiveDuplicate_IsAllowed()
    {
        var store = new HistoryStore(_file);
        store.Remember("a");
        store.Remember("b");
        store.Remember("a"); // 非连续重复：允许（回到常用命令）
        Assert.Equal(3, store.Count);
    }

    [Fact]
    public void Remember_OverCap_DropsOldest()
    {
        var store = new HistoryStore(_file);
        for (int i = 0; i < HistoryStore.MaxEntries + 5; i++)
            store.Remember($"cmd{i}");
        Assert.Equal(HistoryStore.MaxEntries, store.Count);
        Assert.Equal("cmd5", store.Entries[0]); // 最旧的 5 条被丢弃
    }

    [Fact]
    public void Reload_ReadsPersistedEntries()
    {
        var store = new HistoryStore(_file);
        store.Remember("hello");
        store.Remember("world");

        var reloaded = new HistoryStore(_file);
        Assert.Equal(["hello", "world"], reloaded.Entries);
    }

    [Fact]
    public void Load_BlankLines_AreSkipped()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllLines(_file, ["keep", "", "   ", "also"]);
        var store = new HistoryStore(_file);
        Assert.Equal(["keep", "also"], store.Entries);
    }
}
