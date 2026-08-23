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
    public void Remember_NonConsecutiveDuplicate_MovesToEnd()
    {
        // 同一命令再次使用：旧副本移除、条目回到末尾——
        // ↑/↓ 与 Ctrl+R 里不再出现散落的旧副本
        var store = new HistoryStore(_file);
        store.Remember("a");
        store.Remember("b");
        store.Remember("a");
        Assert.Equal(2, store.Count);
        Assert.Equal(["b", "a"], store.Entries); // a 移到末尾
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

    [Fact]
    public void Load_MissingFile_StartsEmpty()
    {
        // 历史文件不存在时从空开始，不抛异常
        var store = new HistoryStore(Path.Combine(_dir, "never-created.txt"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Load_OverCap_KeepsLatestOnly()
    {
        // 磁盘文件超过上限时只保留最新的 MaxEntries 条
        var lines = Enumerable.Range(0, HistoryStore.MaxEntries + 10).Select(i => $"line{i}").ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllLines(_file, lines);

        var store = new HistoryStore(_file);
        Assert.Equal(HistoryStore.MaxEntries, store.Count);
        Assert.Equal("line10", store.Entries[0]); // 最旧的 10 条被丢弃
    }

    [Fact]
    public void Remember_AfterLoad_AppendsNew()
    {
        // 加载旧历史后继续记录：新条目追加到末尾并持久化
        var store = new HistoryStore(_file);
        store.Remember("old1");
        store.Remember("old2");
        var reloaded = new HistoryStore(_file);
        reloaded.Remember("new3");

        Assert.Equal(["old1", "old2", "new3"], reloaded.Entries);
    }

    [Theory]
    [InlineData("/save x")]
    [InlineData("/mode next")]
    [InlineData("/undo")]
    [InlineData("普通输入")]
    public void Remember_SlashCommands_AreStored(string line)
    {
        // 斜杠命令也应进入历史（REPL 里可按 ↑ 复用）
        var store = new HistoryStore(_file);
        store.Remember(line);
        Assert.Equal(1, store.Count);
        Assert.Equal(line, store.Entries[0]);
    }

    [Fact]
    public void Remember_MultiLineEntry_SurvivesRoundtrip()
    {
        // 回归：多行粘贴进入历史后，按行存储的文件曾把一条拆成多条碎片；
        // 现在内嵌换行转义保存，重载后仍是同一条完整条目
        var multi = "line1\nline2\n\nline3";
        var store = new HistoryStore(_file);
        store.Remember(multi);
        store.Remember("after");

        var reloaded = new HistoryStore(_file);
        Assert.Equal(2, reloaded.Count);
        Assert.Equal(multi, reloaded.Entries[0]);
        Assert.Equal("after", reloaded.Entries[1]);
    }

    [Fact]
    public void Remember_EntryWithLiteralBackslashN_Roundtrips()
    {
        // 原文里的字面 \n（两个字符）与真换行必须可区分且各自还原
        var literal = "print(\"a\nb\")";
        var store = new HistoryStore(_file);
        store.Remember(literal);

        var reloaded = new HistoryStore(_file);
        Assert.Equal(literal, reloaded.Entries[0]);
    }

    [Fact]
    public void Load_LegacyFileWithBackslashPaths_Unchanged()
    {
        // 旧版写入的单行条目（Windows 路径含未转义反斜杠）：未识别的转义序列保持原样
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllLines(_file, [@"cd D:\Projects\CodeAgent", "git status"]);
        var store = new HistoryStore(_file);
        Assert.Equal(@"cd D:\Projects\CodeAgent", store.Entries[0]);
        Assert.Equal("git status", store.Entries[1]);
    }
}
