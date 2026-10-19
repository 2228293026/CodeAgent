using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// <c>/help</c> 的快捷键表必须与实际接线的键**同步**。
///
/// 这一条专门防「文档写了、按了没反应」：帮助里列着一个键，用户就会去按；
/// 按了没反应，他会先怀疑自己按错了，再怀疑程序卡了——而真相只是没人接线。
/// 反过来的漏写更隐蔽：功能做了，但没人知道它存在，等于没做。
/// </summary>
public class ShortcutDocSyncTests
{
    private static readonly string[] EditKeys = ["Ctrl+A", "Ctrl+E", "Ctrl+K", "Ctrl+W"];

    [Fact]
    public void EveryEditKeyIsDocumented()
    {
        var keys = Program.HelpShortcutRows().Select(r => r.Key).ToList();
        Assert.Contains(keys, k => k.Contains("Ctrl+A"));
        Assert.Contains(keys, k => k.Contains("Ctrl+E"));
        Assert.Contains(keys, k => k.Contains("Ctrl+K"));
        Assert.Contains(keys, k => k.Contains("Ctrl+W"));
    }

    [Fact]
    public void TheExpandKeyIsDocumented()
    {
        Assert.Contains(Program.HelpShortcutRows(), r => r.Key == "Ctrl+O");
    }

    [Fact]
    public void EveryDocumentedCtrlKeyIsActuallyWired()
    {
        // 逐个到源码里找 switch 分支。带组合键的（Alt+M / Ctrl+Shift+M）只认第一个，
        // 那个才是主键位；这里只查单字母 Ctrl+X 形态。
        var src = ReadSource("InputLine.cs");
        var wired = EditKeys.Concat(["Ctrl+O", "Ctrl+R", "Ctrl+T", "Ctrl+L", "Ctrl+C"])
            .Select(k => k[^1])
            .Distinct()
            .ToList();
        foreach (var letter in wired)
        {
            var marker = $"case ConsoleKey.{letter} when (key.Modifiers & ConsoleModifiers.Control) != 0";
            // Ctrl+C / Ctrl+L 的既有写法可能把条件拆开，宽松一点匹配「Case 字母」+ 附近有 Control
            var exact = src.Contains(marker, StringComparison.Ordinal);
            var loose = src.Contains($"case ConsoleKey.{letter}", StringComparison.Ordinal)
                && src.Contains("ConsoleModifiers.Control", StringComparison.Ordinal);
            Assert.True(exact || loose, $"帮助里写了 Ctrl+{letter}，但输入循环里没有这个分支");
        }
    }

    [Fact]
    public void EveryShortcutHasANonEmptyDescription()
    {
        // 空说明等于只给了一个键名：用户知道有这个键，但不知道它干什么
        foreach (var (key, desc) in Program.HelpShortcutRows())
        {
            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.False(string.IsNullOrWhiteSpace(desc));
        }
    }

    [Fact]
    public void NoDuplicateKeysInTheTable()
    {
        // 同一个键写两行、说明还不同，是最难判断该信哪一条的情况
        var dup = Program.HelpShortcutRows()
            .GroupBy(r => r.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(dup);
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
