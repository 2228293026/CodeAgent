using System;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// @ 文件菜单的条数说明。
///
/// <c>MentionItems</c> 只取前 <c>limit</c>（200）个。此前被丢掉的那部分
/// **一个字都没提**——用户翻到列表底部，看到的就是"全部"，
/// 于是认定某个文件不存在。它其实在，只是排在第 201 名之后。
///
/// 特别容易混淆的是列表里那个 <c>... (+N more)</c>：那是**窗口**滚过去的，
/// 不是被 <c>limit</c> 丢掉的。看到 <c>(+N more)</c> 会以为还能继续滚，
/// 实际上已经到底了。两个上限，必须分开说。
/// </summary>
public sealed class MentionMenuCountTests
{
    private static string MakeWorkspace(int files)
    {
        var root = Path.Combine(Path.GetTempPath(), "codeagent-mc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        for (int i = 0; i < files; i++)
            File.WriteAllText(Path.Combine(root, $"f{i:D4}.txt"), "x");
        return root;
    }

    [Fact]
    public void TotalIsCountedBeforeTheLimitClips()
    {
        var root = MakeWorkspace(250);
        try
        {
            var items = InputLine.MentionItems(root, "", 200, CancellationToken.None, out var total);
            Assert.Equal(200, items.Count);
            // 这是本轮的核心：被丢掉 50 个这件事必须**看得见**
            Assert.Equal(250, total);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void TotalEqualsShownWhenNothingWasDropped()
    {
        var root = MakeWorkspace(3);
        try
        {
            InputLine.MentionItems(root, "", 200, CancellationToken.None, out var total);
            Assert.Equal(3, total);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void TotalIsZeroForAMissingRoot()
    {
        InputLine.MentionItems(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N")),
            "", 200, CancellationToken.None, out var total);
        Assert.Equal(0, total);
    }

    [Theory]
    [InlineData(250, 200)]   // 被截断了
    [InlineData(200, 200)]   // 正好齐平：不算截断
    [InlineData(3, 3)]
    public void NoteSaysSoWhenFilesWereDropped(int total, int shown)
    {
        var note = InputLine.MentionCountNote(total, shown);
        Assert.Contains(total.ToString(), note);
        if (total > shown)
        {
            Assert.Contains(shown.ToString(), note);
            Assert.Contains("缩小范围", note);   // 给出可执行的下一步
        }
    }

    [Fact]
    public void NoteSaysNoMatchesRatherThanZero()
    {
        Assert.Equal("无匹配文件", InputLine.MentionCountNote(0, 0));
    }

    [Fact]
    public void TheHeaderCarriesTheCount()
    {
        var header = InputLine.BuildMenuHeader(modePicker: false, mentionActive: true, mentionTotal: 250, mentionShown: 200);
        Assert.StartsWith("  Files (250", header);
        Assert.Contains("200", header);
    }

    [Fact]
    public void TheHeaderWithoutCountsStillReadsWell()
    {
        // 旧调用方不传条数时不该退化成 "Files (0 个匹配)"——那是**假**的
        var header = InputLine.BuildMenuHeader(modePicker: false, mentionActive: true);
        Assert.DoesNotContain("0 个匹配", header);
        Assert.Contains("Files", header);
    }

    [Fact]
    public void TheHeaderStillDescribesInsertNotRun()
    {
        // @ 菜单是**插入**路径，不是执行命令——此前修过一次，别回退
        var header = InputLine.BuildMenuHeader(modePicker: false, mentionActive: true, 3, 3);
        Assert.Contains("insert", header);
        Assert.DoesNotContain("run", header);
    }

    [Fact]
    public void TheCallSiteThreadsTheTotalIntoTheHeader()
    {
        // 只测纯函数会漏掉"表头其实没拿到总数"——那正是原来没提示的原因
        var src = ReadSource(FindInputLine());
        Assert.Contains("out var mentionTotal", src);
        Assert.Contains("BuildMenuHeader(modePicker, mention.Item1 >= 0, mentionTotal, menuItems.Count)", src);
    }

    private static string ReadSource(string p) => File.ReadAllText(p).Replace("\r\n", "\n");

    private static string FindInputLine()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            for (var i = 0; i < 10 && d.Length > 1; i++)
            {
                var f = Path.Combine(d, "src", "CodeAgent", "InputLine.cs");
                if (File.Exists(f))
                    return f;
                var parent = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == d)
                    break;
                d = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/InputLine.cs");
    }
}
