using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 行内样式在**没有颜色**时的可辨识性。
/// 回归点：行内代码与加粗此前**只靠颜色**区分。颜色一旦关闭
/// （NO_COLOR / TERM=dumb / 输出重定向），两者与普通文本长得一模一样——
/// 信息是被**丢掉**了，而不只是样式没了。
/// 现在关闭颜色时改为还原 Markdown 标记本身（用户当初写的原文）。
/// </summary>
public class InlineStyleWithoutColorTests
{
    private static string Render(string content)
    {
        var original = SafeColor.ReadEnv;
        var writer = new StringWriter();
        var stdout = Console.Out;
        try
        {
            SafeColor.ReadEnv = name => name == "NO_COLOR" ? "1" : null;
            Console.SetOut(writer);
            var renderer = new ConsoleRenderer(true);
            renderer.SetWidth(120);
            renderer.Append(content + "\n");
            renderer.Flush();
        }
        finally
        {
            Console.SetOut(stdout);
            SafeColor.ReadEnv = original;
        }
        return writer.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void CodeIsMarkedWhenColourIsOff()
    {
        var output = Render("调用 `run_command` 可以执行命令。\n");
        Assert.Contains("`run_command`", output);
    }

    [Fact]
    public void BoldIsMarkedWhenColourIsOff()
    {
        var output = Render("这是**很重要**的一句话。\n");
        Assert.Contains("*很重要*", output);
    }

    [Fact]
    public void PlainTextIsNotDecorated()
    {
        var output = Render("普通的一段话。\n");
        Assert.DoesNotContain("*", output);
        Assert.Contains("普通的一段话。", output);
    }

    [Fact]
    public void CodeAndProseRemainDistinguishable()
    {
        // 核心不变式：代码段与散文段不能长得一模一样。
        // 这里有两段代码，所以反引号是 4 个（每段 2 个）——断言的是「成对出现」。
        var output = Render("先 `build`，再 `test`。\n");
        var line = ConsoleRendererTests.OutputLines(output).First(l => l.Contains("build", StringComparison.Ordinal));
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(line, "`").Count);
    }

    [Fact]
    public void EmitsNoEscapeSequencesWhenColourIsOff()
    {
        var output = Render("普通 `代码` 文本\n");
        Assert.DoesNotContain('', output);
    }

    [Fact]
    public void ColourPathStillStripsTheMarkers()
    {
        // 颜色可用时保持干净排版：不还原 Markdown 标记。
        // 必须同时覆盖 IsRedirected——`dotnet test` 下输出本来就被重定向，
        // Enabled 会是 false，于是走的是关闭颜色的分支，测不到这条路径。
        var originalEnv = SafeColor.ReadEnv;
        var originalRedirected = SafeColor.IsRedirected;
        var writer = new StringWriter();
        var stdout = Console.Out;
        try
        {
            SafeColor.ReadEnv = name => null;
            SafeColor.IsRedirected = () => false;
            Console.SetOut(writer);
            var renderer = new ConsoleRenderer(true);
            renderer.SetWidth(120);
            renderer.Append("调用 `run_command` 执行。\n");
            renderer.Flush();
        }
        finally
        {
            Console.SetOut(stdout);
            SafeColor.ReadEnv = originalEnv;
            SafeColor.IsRedirected = originalRedirected;
        }
        var output = writer.ToString().Replace("\r\n", "\n");
        Assert.Contains("run_command", output);
        Assert.DoesNotContain("`", output);
    }

    [Fact]
    public void FallbackCoversEveryStyledToken()
    {
        // 源码级确认：关闭颜色的分支对三种样式都有处理，
        // 不会漏掉某一种（比如将来新增 Underline 又忘了兜底）
        var source = File.ReadAllText(FindRenderer());
        var at = source.IndexOf("private void EmitStyledParts", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var body = source[at..source.IndexOf("/// <summary>", at, StringComparison.Ordinal)];
        foreach (var token in new[] { "InlineStyleToken.Code", "InlineStyleToken.Bold" })
            Assert.Contains(token, body);
        Assert.Contains("SafeColor.Enabled", body);
    }

    private static string FindRenderer()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent", "ConsoleRenderer.cs");
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 1000)
                    return candidate;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/ConsoleRenderer.cs");
    }
}
