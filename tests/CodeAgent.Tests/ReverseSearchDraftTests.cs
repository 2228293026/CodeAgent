using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// Ctrl+R 反向搜索与「正在编辑的草稿」的冲突。
///
/// 用户打了半句、按 Ctrl+R 找一条旧命令参考，翻了几条发现都不是，Esc 退出——
/// 那半句还在吗？
///
/// 进入搜索时只清 searchQuery，**没有保存 buf**。一旦找到命中，SetBuf 就把 buf 覆写掉，
/// 原草稿从此没有任何地方存放。Esc 的注释写着「保留当前命中的文本」，
/// 于是用户拿到的是那条历史，自己打的字消失了。
///
/// ↑/↓ 浏览历史那条路径处理得是对的：先 `if (draft is null) draft = buf.Text;`。
/// 搜索这条路漏了同一步——机制本来就该一致。
/// </summary>
[Collection("ConsoleOutput")]
public class ReverseSearchDraftTests
{
    [Fact]
    public void SearchEntrySavesTheDraftBeforeOverwriting()
    {
        var src = ReadInputLine();
        var at = src.IndexOf("case ConsoleKey.R when", StringComparison.Ordinal);
        Assert.True(at > 0, "没找到 Ctrl+R 分支");
        var end = src.IndexOf("case ConsoleKey.L when", at, StringComparison.Ordinal);
        Assert.True(end > at, "没找到 Ctrl+R 分支的结束边界");
        var block = src[at..end];
        // 进入搜索（!searching 那一支）必须在动 buf 之前留住草稿
        Assert.Contains("draft", block);
    }

    [Fact]
    public void DraftIsOnlyCapturedWhenNotAlreadyBrowsing()
    {
        // 已经在浏览历史时 draft 里存的是「浏览之前」的草稿，不能被当前 buf（某条历史）覆盖。
        // 这条规矩在 ↑/↓ 分支里靠 `draft is null` 保证，搜索也必须照办。
        var src = ReadInputLine();
        var hits = CountOccurrences(src, "if (draft is null)");
        Assert.True(hits >= 2, $"草稿捕获应出现在多处（历史浏览 + 搜索），实际 {hits} 处");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    private static string ReadInputLine()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(dir, "src", "CodeAgent", "InputLine.cs");
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return System.IO.File.ReadAllText(f);
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 InputLine.cs");
    }
}
