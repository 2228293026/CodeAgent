using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 流式「逐字长出来」的原地重绘。
///
/// 回归：<see cref="ConsoleRenderer"/> 把字符攒进行缓冲，**凑齐一整行才写出去**。
/// 模型是逐字返回的，于是屏幕一直不动，直到整行结束才「啪」地蹦出一整行——
/// 用户看到的是「卡很久，然后一行行跳出来」，而不是字在长出来。
///
/// 这条路径的行为必须**两条腿都钉住**：
/// 1. 打开时半行真的会画出去，且每次重绘只覆盖一行（\r + ESC[2K）；
/// 2. 关闭时输出与旧版逐字节相同（管道/重定向/关掉 Markdown 全走这条）。
/// 只钉住前者，就会有人为了「动态」把管道里也塞满控制序列。
/// </summary>
[Collection("ConsoleOutput")]
public class LivePreviewStreamTests
{
    private const string ClearLine = "\r\x1b[2K";

    private static string Capture(Action action)
    {
        var prev = Console.Out;
        var writer = new StringWriter { NewLine = "\n" };
        Console.SetOut(writer);
        try { action(); }
        finally { Console.SetOut(prev); writer.Flush(); }
        return writer.ToString();
    }

    private static ConsoleRenderer Renderer(bool livePreview, int width = 80)
    {
        var r = new ConsoleRenderer(enabled: true) { LivePreview = livePreview };
        r.SetWidth(width);
        return r;
    }

    [Fact]
    public void CharsAppearBeforeTheLineEnds()
    {
        // 这是本轮的核心诉求：换行还没到，字就必须已经在屏幕上。
        var r = Renderer(livePreview: true);
        var first = Capture(() => r.Append("你"));
        Assert.Equal("你", first);
        var second = Capture(() => r.Append("好"));
        // 第二帧必须先把上一帧擦掉，否则屏幕上会是「你你好」
        Assert.Equal(ClearLine + "你好", second);
    }

    [Fact]
    public void EveryFrameOverwritesInsteadOfAppending()
    {
        // 逐帧检查「先擦自己、再画自己」：任何一帧漏掉 \r 或 ESC[2K，
        // 屏幕上都会留下半截残字，而这种残留只在真终端、且只在长回复时才看得见。
        var r = Renderer(livePreview: true);
        var text = "这是一段会一点点长出来的中文";
        var i = 0;
        foreach (var ch in text)
        {
            i++;
            var output = Capture(() => r.Append(ch.ToString()));
            // 首帧没有上一帧可擦，本来就不该有清除序列；从第二帧起必须每帧都先擦。
            var expected = (i == 1 ? "" : ClearLine) + text[..i];
            Assert.Equal(expected, output);
        }
    }

    [Fact]
    public void LineEndingClearsThePreviewBeforeEmit()
    {
        // 收行时：先擦预览，再按最终样式整行输出。
        // 不擦就会「预览 + 正式」两份同一行内容并排出现在屏幕上。
        var r = Renderer(livePreview: true);
        Capture(() => r.Append("半行"));
        var emitted = Capture(() => r.Append("\n"));
        Assert.StartsWith(ClearLine, emitted, StringComparison.Ordinal);
        Assert.Contains("半行", emitted);
        Assert.EndsWith("\n", emitted, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewNeverBecomesPartOfTheFinalOutput()
    {
        // 预览是「临时视觉」，不能污染屏幕上的最终内容。
        //
        // 断言方式必须**按终端语义回放**，不能把控制序列删掉再拼字符串：
        // 预览帧本来就是重复写同一行的，拼起来是「你你好你好，…」这种叠加结果，
        // 那样断言只会测出「我删得不对」，测不到屏幕上到底留下了什么。
        var r = Renderer(livePreview: true);
        var all = new StringBuilder();
        foreach (var ch in "你好，世界\n第二行\n")
            all.Append(Capture(() => r.Append(ch.ToString())));
        Assert.Equal("你好，世界\n第二行", ReplayTerminal(all.ToString()));
    }

    /// <summary>把一段终端输出按 `\r` / ESC[2K / `\n` 的语义回放到屏幕上，返回最终画面。
    /// 只实现这几条控制序列——本类要验证的也只有这几条。</summary>
    private static string ReplayTerminal(string output)
    {
        var lines = new List<StringBuilder> { new() };
        var row = 0;
        var col = 0;
        for (var i = 0; i < output.Length; i++)
        {
            var c = output[i];
            if (c == '\r')
            {
                col = 0;
                continue;
            }
            if (c == '\x1b' && i + 1 < output.Length && output[i + 1] == '[')
            {
                var j = i + 2;
                while (j < output.Length && !char.IsLetter(output[j]))
                    j++;
                if (j < output.Length && output[j] == 'K')
                    lines[row].Clear(); // ESC[2K：清整行（光标不动）
                i = j;
                continue;
            }
            if (c == '\n')
            {
                if (row == lines.Count - 1)
                    lines.Add(new StringBuilder());
                row++;
                col = 0;
                continue;
            }
            var sb = lines[row];
            while (sb.Length < col)
                sb.Append(' ');
            if (col < sb.Length)
                sb[col] = c;
            else
                sb.Append(c);
            col++;
        }
        return string.Join("\n", lines.Select(l => l.ToString())).TrimEnd('\n');
    }

    [Fact]
    public void DisabledPreviewWritesNothingUntilTheLineIsComplete()
    {
        // 关闭时与旧行为逐字节相同：半行一个字节都不写。
        var r = Renderer(livePreview: false);
        Assert.Equal("", Capture(() => r.Append("你")));
        Assert.Equal("", Capture(() => r.Append("好")));
        Assert.Equal("你好\n", Capture(() => r.Append("\n")));
    }

    [Fact]
    public void UnknownWidthDoesNotPreview()
    {
        // 宽度未知时无法保证「不折行」，而清行只作用一行——
        // 一次折行就会留下擦不掉的残字，所以宁可不预览。
        var r = Renderer(livePreview: true, width: 0);
        Assert.Equal("", Capture(() => r.Append("你")));
    }

    [Fact]
    public void OverlongPreviewIsClippedToStayOnOneLine()
    {
        // 预览超宽时必须截断（预留 1 列给光标）：折行会让清行只清掉最后一行的开头。
        var r = Renderer(livePreview: true, width: 10);
        var output = Capture(() => r.Append(new string('x', 50)));
        Assert.True(TextUtil.DisplayWidth(output.Replace(ClearLine, "")) <= 9,
            $"预览超宽会折行，清理不干净：[{output}]");
    }

    [Fact]
    public void CodeLinesAppearWithoutWaitingForTheFenceToClose()
    {
        // 代码块此前攒满整块才吐：生成 60 行代码时前 59 行都看不到任何动静。
        // 现在一行到齐就落一行——围栏还没关，代码已经在屏幕上。
        var r = Renderer(livePreview: true);
        var all = new StringBuilder();
        all.Append(Capture(() => r.Append("```cs\n")));
        all.Append(Capture(() => r.Append("var x = 1;\n")));
        // 此时围栏**尚未关闭**，但这一行代码必须已经出去
        Assert.Contains("var x = 1;", all.ToString());
        all.Append(Capture(() => r.Append("```\n")));
    }

    [Fact]
    public void IncompleteCodeLineIsPreviewedButNeverCommitted()
    {
        // 半行代码要能看到（长行不能卡着不动），但它只是**可擦除的预览**：
        // 不能以正式行的形式落地，否则 `var` 和 `x` 会被拆成两行存进代码块。
        var r = Renderer(livePreview: true);
        Capture(() => r.Append("```\n"));
        var half = Capture(() => r.Append("var x"));
        // 画出来了
        Assert.Contains("var x", half);
        // 但整帧是一行高、可被 \r + ESC[2K 擦掉（不能带换行，否则擦不干净）
        Assert.DoesNotContain('\n', half);
        // 续行到达时它作为**同一行**收掉，而不是先落一次 "var x\n" 再落 "= 1;\n"
        var rest = Capture(() => r.Append(" = 1;\n"));
        var replayed = ReplayTerminal(half + rest);
        Assert.Contains("var x = 1;", replayed);
    }

    [Fact]
    public void FormalHalfLineIsNeverPaintedOverByAPreview()
    {
        // Flush 会把**不以换行结尾**的半行原样写出去，此时光标停在行中。
        // 若接着画预览，下一帧的 \r + ESC[2K 会把刚写出去的正式内容一起擦掉——
        // 用户会看到答案的最后半句凭空消失，而且只在真终端、只在模型收尾不带换行时出现。
        var r = Renderer(livePreview: true);
        Capture(() => r.Append("最后半句"));
        var flushed = Capture(() => r.Flush());
        Assert.Contains("最后半句", flushed);
        // 关键：半行落地后光标在行中，下一段内容**不能**以预览形式覆盖它
        // 必须喂**两段**才暴露：第一段只是把预览画上去（没有清除序列），
        // 真正的破坏发生在下一帧——它会先 `\r` 回行首再 ESC[2K，
        // 把那半行正式内容连同预览一起抹掉。
        Capture(() => r.Append("续写"));
        var second = Capture(() => r.Append("更多"));
        Assert.DoesNotContain(ClearLine, second);
    }

    [Fact]
    public void ExternalWritersCanEraseThePreviewFirst()
    {
        // 渲染器只包得住自己的写入。工具状态行、回合摘要都是 Agent 直接 Console.WriteLine 的，
        // 模型最后一段不带换行时预览还挂在光标行上——不先擦，那些行会接在预览尾巴后面，
        // 屏幕留下一截永远擦不掉的残字（只在真终端可见）。
        var r = Renderer(livePreview: true);
        Capture(() => r.Append("半行答案"));
        var cleared = Capture(() => r.ClearLivePreview());
        Assert.Equal(ClearLine, cleared);
        // 幂等：没有预览时一个字节都不写，否则调用方每写一行都会多吐一次清行序列
        Assert.Equal("", Capture(() => r.ClearLivePreview()));
    }

    [Fact]
    public void ExternalMidLineWritePausesThePreview()
    {
        // 思考文本是逐段裸写的、不带换行。擦掉预览却不告诉渲染器「光标现在在行中」，
        // 下一帧就会 \r 回行首，把刚写出去的思考文本一起抹掉。
        var r = Renderer(livePreview: true);
        Capture(() => r.Append("半行正文"));
        // 外部写入一段不带换行的思考文本
        Assert.Equal(ClearLine, Capture(() => r.NotifyExternalWrite(endedWithNewline: false)));
        // 之后**不能**再画预览：光标不在行首，重绘会擦掉那段思考
        Assert.Equal("", Capture(() => r.Append("新内容")));
    }

    [Fact]
    public void PreviewResumesAfterALineLands()
    {
        // 暂停必须是暂时的：下一行正式内容落地、光标回到行首后，预览要自动恢复，
        // 否则一次思考文本就会让整轮回复失去逐字效果。
        var r = Renderer(livePreview: true);
        Capture(() => r.NotifyExternalWrite(endedWithNewline: false));
        Assert.Equal("", Capture(() => r.Append("新内容"))); // 行中：不预览
        Capture(() => r.Append("\n"));                        // 换行落地，光标回行首
        var after = Capture(() => r.Append("后面"));
        // 恢复预览：新一行开始重绘。首帧没有上一帧要擦，所以这里不该有清除序列——
        // 有的话反而说明它在擦一个不存在的预览（多吐一次 ESC[2K）。
        Assert.Equal("后面", after);
        // 真正的证据是「第二帧开始逐字覆盖」：这只有在预览恢复后才发生
        var third = Capture(() => r.Append("再续"));
        Assert.Equal(ClearLine + "后面再续", third);
    }

    [Fact]
    public void EveryFrameIsClearableByOneLineErase()
    {
        // 不变式：一帧一帧重绘必须**始终只有一行高**。
        // 只要有一帧带了换行，\r + ESC[2K 就只能清掉最后一行的开头，
        // 上一帧的残字会永久留在屏幕上——这类脏屏幕只在真终端可见，测试抓不住就别指望发现。
        var r = Renderer(livePreview: true);
        foreach (var ch in "abcdefghij")
        {
            var frame = Capture(() => r.Append(ch.ToString()));
            Assert.DoesNotContain('\n', frame);
        }
    }
}
