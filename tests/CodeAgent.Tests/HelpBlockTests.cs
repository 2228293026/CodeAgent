using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// <c>/help</c> 的用法/参数/快捷键区块。
///
/// 此前它是一整段**原始字符串字面量**，两个问题：
/// ① 不受宽度约束——上半屏命令列表走 FormatHelpList 精心排版，下半屏却硬折行；
/// ② <b>注释占位符漏进了输出</b>：原始字符串里 <c>{SafeColor.Glyphs.Arrow}</c> 不是插值，
///    用户实际看到的是 <c>strict{SafeColor.Glyphs.Arrow}whitelist{…}full</c>。
/// </summary>
public sealed class HelpBlockTests
{
    [Fact]
    public void ShortcutRow_ShowsRealGlyphsNotAPlaceholder()
    {
        var row = Program.HelpShortcutRows().First(r => r.Key == "Shift+Tab");
        Assert.Contains("strict", row.Desc);
        Assert.Contains("whitelist", row.Desc);
        Assert.Contains("full", row.Desc);
        // 核心回归：占位符曾经原样打给用户
        Assert.DoesNotContain("{SafeColor", row.Desc);
        Assert.DoesNotContain("{", row.Desc);
    }

    [Fact]
    public void ShortcutRow_UsesTheActualGlyph()
    {
        var row = Program.HelpShortcutRows().First(r => r.Key == "Shift+Tab");
        Assert.Contains(SafeColor.Glyphs.Arrow, row.Desc);
    }

    [Fact]
    public void NoRowLeavesACodePlaceholder()
    {
        foreach (var (key, desc) in Program.HelpShortcutRows().Concat(Program.HelpOptionRows()).Concat(Program.HelpUsageRows()))
        {
            Assert.DoesNotContain("{SafeColor", desc);
            Assert.DoesNotContain("Glyphs.", desc);
            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.False(string.IsNullOrWhiteSpace(desc));
        }
    }

    [Fact]
    public void CtrlTIsDocumented()
    {
        Assert.Contains(Program.HelpShortcutRows(), r => r.Key == "Ctrl+T" && r.Desc.Contains("思考强度"));
    }

    [Fact]
    public void TablesAreKeyedAndDescribed()
    {
        // 键列要对齐才能排版好看：不允许任何一行缺说明
        foreach (var row in Program.HelpShortcutRows().Concat(Program.HelpOptionRows()).Concat(Program.HelpUsageRows()))
            Assert.False(string.IsNullOrWhiteSpace(row.Item2), $"{row.Item1} 缺说明");
    }

    [Fact]
    public void KeysAreUniquePerTable()
    {
        // 重复键位会让读者以为是两回事
        foreach (var table in new[] { Program.HelpShortcutRows(), Program.HelpOptionRows(), Program.HelpUsageRows() })
        {
            var keys = table.Select(r => r.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void ProgramHasNoRawStringThatWouldLeakPlaceholders()
    {
        // 结构性守卫：原始字符串（无 $ 前缀）里出现 {SafeColor.…} 就会原样打给用户。
        // 这类占位符是**文档注释的写法**，写进字面量时不会有人注意到。
        var src = System.IO.File.ReadAllText(Find());
        var leaks = new System.Collections.Generic.List<string>();
        foreach (Match m in Regex.Matches(src, "(?s)\"\"\"(.*?)\"\"\""))
        {
            if (m.Groups[1].Value.StartsWith("$", StringComparison.Ordinal))
                continue; // 插值原始字符串：占位符会被求值，安全
            foreach (Match hit in Regex.Matches(m.Groups[1].Value, @"\{SafeColor[^}]*\}"))
                leaks.Add(hit.Value);
        }
        Assert.True(leaks.Count == 0, "原始字符串（非插值）里含会原样输出的占位符：" + string.Join(" | ", leaks.Distinct()));
    }

    [Fact]
    public void ReplHelpUsesTheTables()
    {
        // 只测数据表会漏掉"PrintReplHelp 压根没用它"——那正是原来的排版不一致
        var src = System.IO.File.ReadAllText(Find());
        Assert.Contains("PrintHelpTable(\"快捷键:\"", src);
        Assert.Contains("PrintHelpTable(\"参数:\"", src);
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
