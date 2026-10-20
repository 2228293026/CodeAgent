using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 回显用户输入 / 异常消息的通知行必须有宽度约束。
/// 回归点：这些行的正文长度**不受控**——`rest` 是用户敲的任意文本，
/// `ex.Message` 可能带整条文件路径或嵌套异常。此前一律
/// `Console.WriteLine($"…")`，没有宽度预算，窄终端上会硬折行把提示符顶走。
/// 现在统一走 <see cref="Program.WriteNotice"/> 这一个出口。
/// </summary>
public class UnboundedNoticeLineTests
{
    private static string[] Lines(string s) => s.Replace("\r\n", "\n").Split('\n');

    [Fact]
    public void LongUserInputIsBounded()
    {
        // 用户在 /thinking 后敲 5000 个字符——旧实现会把它整行打出去
        var rest = new string('x', 5000);
        var line = Program.FormatNoticeLine($"无效值: {rest}（可选: off / low / medium / high / auto）", 60, "⚠");
        Assert.True(TextUtil.DisplayWidth(line) <= 60, $"宽度 {TextUtil.DisplayWidth(line)}");
        Assert.Contains('…', line);
    }

    [Fact]
    public void LongExceptionMessageIsBounded()
    {
        var ex = new InvalidOperationException("x" + new string('y', 3000));
        var line = Program.FormatNoticeLine($"切换失败: {ex.Message}", 40, "⚠");
        Assert.True(TextUtil.DisplayWidth(line) <= 40, line);
    }

    [Fact]
    public void UnknownWidthIsUnchanged()
    {
        var line = Program.FormatNoticeLine("保存失败: boom", 0, "⚠");
        Assert.Equal("⚠ 保存失败: boom", line);
    }

    [Fact]
    public void TrailingPathInAMessageKeepsItsTail()
    {
        // 异常消息常以路径结尾：按尾部保留缩短，别把文件名切掉
        var line = Program.FormatNoticeLine("写入配置失败: 拒绝访问 C:/Users/me/AppData/Local/Temp/codeagent/config.json", 40, "⚠");
        Assert.Contains("config.json", line);
        Assert.True(TextUtil.DisplayWidth(line) <= 40, line);
    }

    [Fact]
    public void EveryNotifiedLineFitsAcrossWidths()
    {
        var bodies = new[]
        {
            "无效值: " + new string('a', 200) + "（可选: off / low / medium / high / auto）",
            "切换失败: " + new string('b', 200),
            "  相近的模式: " + string.Join("、", Range(30).Select(i => $"模式{i}")),
        };
        foreach (var width in new[] { 12, 20, 40, 80 })
        {
            foreach (var body in bodies)
            {
                foreach (var line in Lines(Program.FormatNoticeLine(body, width, "⚠")))
                    Assert.True(TextUtil.DisplayWidth(line) <= width, $"预算 {width}：{line}");
            }
        }
    }

    [Fact]
    public void CjkInputIsBoundedByDisplayWidth()
    {
        var line = Program.FormatNoticeLine("无效值: " + new string('无', 100), 40, "⚠");
        Assert.True(TextUtil.DisplayWidth(line) <= 40, line);
    }

    [Fact]
    public void WriteNoticeIsTheSingleChokePoint()
    {
        // 守卫：这些回显用户输入的行必须经过 WriteNotice（宽度收口），
        // 而不是裸的 Console.WriteLine($"…")。
        //
        // 「无效值 / 无效权限模式」两条已改成 FormatInvalidValueLine（Round 270 统一了
        // 四种标点风格），所以这里锚定它们**新的**入口，而不是那句已经删掉的字面量——
        // 断言一段被删掉的文本，既会在改动时报错，也证明不了"是否走了收口"。
        var source = File.ReadAllText(FindSource("Program.cs"));
        foreach (var echo in new[]
        {
            // Round 316 起取值报错走两行入口 FormatInvalidValueLines（自带候选行）
            "FormatInvalidValueLines(\"无效值\", rest, ThinkingEfforts",
            "FormatInvalidValueLines(\"无效值\", rest, ShellNames",
            "FormatInvalidValueLines(\"无效权限模式\", rest, FileAccessModes",
            "模型列表中没有「{modelArg}」",
            "切换失败: {ex.Message}",
            "保存失败: {ex.Message}",
            "加载失败: {ex.Message}",
            "导出失败: {ex.Message}",
        })
        {
            Assert.Contains(echo, source);
            var at = source.IndexOf(echo, StringComparison.Ordinal);
            var before = source.LastIndexOf("Console.WriteLine", at, StringComparison.Ordinal);
            Assert.True(before < 0 || before < at - 60, $"「{echo}」仍在裸 Console.WriteLine 里");
        }
    }

    [Fact]
    public void NoHandWrittenWarnPrefixOutsideTheNoticeRenderer()
    {
        // 守卫：**整个源码树**里不许再有 `$"{SafeColor.Glyphs.Warn} 正文…"` 这种手写告警行。
        //
        // 第一版只扫 Program.cs，于是 Agent.cs 里三处（max_tokens 截断、请求重试、
        // 历史已压缩）全漏了——它们的形态一模一样，只是住得远一点。
        // 守卫的覆盖面必须和「这一族可能出现的地方」一致，否则它给出的绿灯是假的。
        //
        // 这一族的问题：每处自己拼标记，于是
        //   · 没有宽度收口，长提示在窄终端折行，把后续输出推歪；
        //   · 和 FormatNoticeLine 的标记重复（裸叹号提示曾显示成「⚠ ⚠ …」）；
        //   · 改文案时得挨个改，改漏一处就两种说法并存。
        //
        // 唯一合法的写法是 FormatNoticeLine(...) / WriteNotice(...)，它们统一管标记与宽度。
        // 用正则扫而不是逐条锚定：这一族会随功能增加而变长，
        // 逐条列表永远落后于实际。
        var offenders = new List<string>();
        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file).Replace("\r\n", "\n");
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("$\"{SafeColor.Glyphs.Warn} ", StringComparison.Ordinal)
                    && lines[i].Contains("Console.WriteLine", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)} 第 {i + 1} 行: {lines[i].Trim()}");
            }
        }
        Assert.True(offenders.Count == 0,
            "这些告警行还在手写标记（应走 FormatNoticeLine/WriteNotice）：\n" + string.Join("\n", offenders));
    }

    /// <summary>源码树下所有 .cs 文件（守卫要覆盖整个树，不能只看 Program.cs）。</summary>
    private static IEnumerable<string> SourceFiles()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir.Length > 1; i++)
        {
            var candidate = Path.Combine(dir, "src", "CodeAgent");
            if (Directory.Exists(candidate))
                return Directory.EnumerateFiles(candidate, "*.cs", SearchOption.AllDirectories).ToList();
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? dir;
        }
        throw new DirectoryNotFoundException("src/CodeAgent");
    }

    [Fact]
    public void NoUnboundedChineseOutputLine()
    {
        // 守卫：Program.cs 里不许再有「裸 Console.WriteLine($"中文…")」这种无宽度收口的输出。
        //
        // 这一族散落在 8 处（对话历史标题、当前 Provider、无效编号、撤销提示、
        // 系统提示标题…），共性是**都不是错误也不需要标记**，于是当初没走
        // FormatNoticeLine / FormatResultLine——但那些函数管的正是「按宽度收口」，
        // 与是不是错误无关。结果就是窄终端上它们自己折行，把紧跟其后的
        // 对齐列表（/history、/sessions、/undo list）整体推歪。
        //
        // 例外：纯 ASCII 的版本号/路径回显不受影响，所以只扫含中文的行。
        // 另排除固定短句（「已中断」这类 3 个字的常量和版本横幅）——
        // 它们不随用户输入变长，加宽度收口只是噪音。
        var shortLiterals = new[] { "已中断", "LLM 驱动的编码助手 CLI" };
        var source = File.ReadAllText(FindSource("Program.cs")).Replace("\r\n", "\n");
        var offenders = source
            .Split('\n')
            .Select((line, i) => (line, i))
            .Where(x =>
            {
                var t = x.line.Trim();
                if (!t.StartsWith("Console.WriteLine($\"", StringComparison.Ordinal))
                    return false;
                if (t.Contains("ConsoleColumns()", StringComparison.Ordinal))
                    return false;
                if (shortLiterals.Any(s => t.Contains(s, StringComparison.Ordinal)))
                    return false;
                return t.Any(c => c >= '\u4e00' && c <= '\u9fff');
            })
            .Select(x => $"第 {x.i + 1} 行: {x.line.Trim()}")
            .ToList();
        Assert.True(offenders.Count == 0,
            "这些输出行没有宽度收口（应走 FormatResultLine/FormatNoticeLine）：\n" + string.Join("\n", offenders));
    }

    private static string FindSource(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir.Length > 1; i++)
        {
            var candidate = Path.Combine(dir, "src", "CodeAgent", name);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? dir;
        }
        throw new FileNotFoundException(name);
    }

    private static IEnumerable<int> Range(int count) => Enumerable.Range(0, count);
}
