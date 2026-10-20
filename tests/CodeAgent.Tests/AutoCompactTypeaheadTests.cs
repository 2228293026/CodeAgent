using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 自动压缩 / /compact 期间，用户提前敲入的下一句会不会丢。
///
/// 机制上有两条路能把 typeahead 清掉：
/// ① <c>RunTurnAsync</c> 开头 <c>_typeahead.Clear()</c>；
/// ② <c>MergeTypeahead</c> 取走内容（回合结束后的循环顶部）。
///
/// ②是**取走**，内容进了草稿。但自动压缩在回合结束后**再开一次** RunTurnAsync，
/// 此时 ② 还没跑——① 先执行就把字丢了。用户等模型几十秒顺手打下一句是常态，
/// 而他无法预判「这次会触发自动压缩，所以我的输入没了」。
/// </summary>
public class AutoCompactTypeaheadTests
{
    [Fact]
    public void ClearingIsOptOut()
    {
        // RunTurnAsync 必须带 preserveTypeahead 开关：
        // 正常回合清（不留上一回合残留），回合后续操作不清（那些字还没被取走）。
        var src = ReadProgram();
        var at = src.IndexOf("private static async Task<string> RunTurnAsync", StringComparison.Ordinal);
        Assert.True(at > 0, "没找到 RunTurnAsync");
        var head = src[at..(at + 400)];
        Assert.Contains("preserveTypeahead", head);
    }

    [Fact]
    public void BothCompactionCallSitesPreserveTypeahead()
    {
        // 自动压缩 + /compact 两个调用点都必须传 true。
        // 只改一个 = 另一条路径仍然静默吞字，而且更隐蔽（自动压缩不常触发）。
        var src = ReadProgram();
        var hits = CountOccurrences(src, "preserveTypeahead: true");
        Assert.True(hits >= 2, $"压缩相关调用点应至少 2 处保留 typeahead，实际 {hits}");
    }

    [Fact]
    public void MergeTypeaheadTakesContentOut()
    {
        // ② 是取走：取过之后缓冲为空，且内容进了草稿
        Program.AppendTypeaheadForTest("下一句");
        try
        {
            var merged = Program.MergeTypeahead(null);
            Assert.Equal("下一句", merged);
            Assert.Equal(0, Program.QueuedChars()); // 取走，不是复制
        }
        finally
        {
            _ = Program.MergeTypeahead(null);
        }
    }

    [Fact]
    public void MergeAppendsToAnExistingDraft()
    {
        // 草稿里已有字时不能覆盖：那是用户更早打的
        Program.AppendTypeaheadForTest("新的一行");
        try
        {
            var merged = Program.MergeTypeahead("已有一半");
            Assert.Equal("已有一半\n新的一行", merged);
        }
        finally
        {
            _ = Program.MergeTypeahead(null);
        }
    }

    [Fact]
    public void QueuedCharsReportsWhatIsPending()
    {
        Assert.Equal(0, Program.QueuedChars());
        Program.AppendTypeaheadForTest("abc");
        try
        {
            Assert.Equal(3, Program.QueuedChars());
        }
        finally
        {
            _ = Program.MergeTypeahead(null);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    private static string ReadProgram()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return System.IO.File.ReadAllText(f);
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 Program.cs");
    }
}
