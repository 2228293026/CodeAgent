using System;
using System.IO;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 「标签: 当前值（可选: a / b / c）」的渲染。
///
/// 此前 <c>/thinking</c> 与 <c>/shell</c> 各写一行**裸插值**，两者都不受宽度约束：
/// 40 列终端上实测分别溢出 8 列与 34 列，整行硬折行。
/// 同类的非法取值提示早已收口到 FormatInvalidValueLine，唯独这两个"当前值"漏了。
/// </summary>
public sealed class SettingWithOptionsLineTests
{
    private static readonly string[] Efforts = ["off", "low", "medium", "high", "auto"];

    [Fact]
    public void FullFormAtGenerousWidth()
    {
        Assert.Equal("思考强度: high（可选: off / low / medium / high / auto）",
            Program.FormatSettingWithOptionsLine("思考强度", "high", Efforts, 120));
    }

    [Fact]
    public void TooNarrow_DropsTheOptionsButKeepsTheValue()
    {
        // 用户刚敲 /thinking，要看的正是当前档位——它必须留下
        var line = Program.FormatSettingWithOptionsLine("思考强度", "high", Efforts, 40);
        Assert.Equal("思考强度: high", line);
    }

    [Fact]
    public void EvenNarrower_StillKeepsSomethingRecognisable()
    {
        // 12 列放不下"思考强度: high"（16 列），只剩得下前缀的一部分——
        // 极端收窄的既定取舍是"宁可截短也不丢整行"，这里守住"非空 + 不超宽"即可
        var line = Program.FormatSettingWithOptionsLine("思考强度", "high", Efforts, 12);
        Assert.NotEqual(string.Empty, line);
        Assert.True(TextUtil.DisplayWidth(line) <= 12, line);
        Assert.Contains("思考", line);
    }

    [Fact]
    public void NeverExceedsTheBudget()
    {
        foreach (var w in new[] { 8, 12, 16, 20, 30, 40, 60, 80, 120 })
        {
            var line = Program.FormatSettingWithOptionsLine("命令 shell", "auto（当前生效: bash）",
                ["cmd", "powershell", "pwsh", "bash", "sh", "auto"], w);
            Assert.True(TextUtil.DisplayWidth(line) <= w, $"宽度 {w} 超宽：{line}");
        }
    }

    [Fact]
    public void UnknownWidthKeepsEverything()
    {
        // width<=0 = 宽度未知，不裁剪（这条语义反复被栽）
        Assert.Equal("思考强度: high（可选: off / low / medium / high / auto）",
            Program.FormatSettingWithOptionsLine("思考强度", "high", Efforts, 0));
    }

    [Fact]
    public void NoOptions_JustTheLabelAndValue()
    {
        Assert.Equal("思考强度: high", Program.FormatSettingWithOptionsLine("思考强度", "high", [], 80));
    }

    [Fact]
    public void OptionsUseTheSameSeparatorAsTheInvalidValueLine()
    {
        // 两处都是"可选值列表"，分隔符必须一致（"/" 而非 "|"）
        var here = Program.FormatSettingWithOptionsLine("x", "y", ["a", "b"], 120);
        var there = Program.FormatInvalidValueLine("x", "y", ["a", "b"], 120);
        Assert.Equal(there.Substring(there.IndexOf('（')), here.Substring(here.IndexOf('（')));
    }

    [Fact]
    public void BothCommandsRouteThroughIt()
    {
        // 只测纯函数会漏掉"/thinking、/shell 压根没用它"——那正是原来溢出的原因。
        // 先剥掉注释再查：这两个旧写法**恰好被新函数的文档注释引用**为对照示例，
        // 不剥注释的话这条守卫会靠文档把自己绊倒（而且分不清是真代码还是注释）。
        var code = StripComments(File.ReadAllText(FindSource()));
        Assert.Equal(3, Occurrences(code, "FormatSettingWithOptionsLine(")); // 定义 + 两个调用点
        Assert.DoesNotContain("$\"思考强度: {config.ThinkingEffort}", code);
        Assert.DoesNotContain("$\"命令 shell: {current}", code);
    }

    [Fact]
    public void TheAutoHintIsAlsoBounded()
    {
        // /thinking 第二行是长说明，同样曾是无宽度的裸字面量
        var code = StripComments(File.ReadAllText(FindSource()));
        Assert.DoesNotContain("Console.WriteLine(\"auto: 自动探测模型支持的档位", code);
    }

    /// <summary>去掉行注释与块注释，避免文档里引用的旧代码被当成活代码。</summary>
    private static string StripComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        foreach (var raw in src.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw;
            var at = line.IndexOf("///", StringComparison.Ordinal);
            if (at >= 0)
                line = line[..at];
            else
            {
                var at2 = line.IndexOf("//", StringComparison.Ordinal);
                if (at2 >= 0)
                    line = line[..at2];
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (string.CompareOrdinal(haystack, i, needle, 0, needle.Length) == 0)
                n++;
        return n;
    }

    private static string FindSource()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (File.Exists(f) && new FileInfo(f).Length > 1000)
                    return f;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
