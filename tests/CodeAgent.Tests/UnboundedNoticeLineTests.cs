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
        var source = File.ReadAllText(FindSource("Program.cs"));
        foreach (var echo in new[]
        {
            "无效值: {rest}",
            "无效权限模式: {rest}",
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
