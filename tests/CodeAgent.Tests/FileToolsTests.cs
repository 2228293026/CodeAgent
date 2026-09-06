using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public class FileToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-tools-" + Guid.NewGuid().ToString("N"));

    public FileToolsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    private static AgentContext MakeContext(string dir) => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(dir),
    };

    [Fact]
    public async Task ReadFile_TailReadsLastLines()
    {
        // tail 模式：读末尾 N 行（日志排查），行号保持全局编号
        File.WriteAllLines(Path.Combine(_dir, "log.txt"), Enumerable.Range(1, 10).Select(i => $"行{i}"));
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var result = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "log.txt", ["tail"] = 3 },
            ctx, CancellationToken.None);

        Assert.Contains("8\t行8", result);
        Assert.Contains("9\t行9", result);
        Assert.Contains("10\t行10", result);
        Assert.DoesNotContain("行7\n", result + "\n"); // 之前的行不出现
        Assert.Contains("已显示 8-10", result);          // 范围提示
    }

    [Fact]
    public async Task ReadFile_TailLargerThanFile_ReadsWholeFile()
    {
        File.WriteAllText(Path.Combine(_dir, "small.txt"), "a\nb\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var result = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "small.txt", ["tail"] = 100 },
            ctx, CancellationToken.None);

        Assert.Contains("a", result);
        Assert.Contains("b", result);
        Assert.DoesNotContain("已显示", result); // 全量显示无范围提示
    }

    [Fact]
    public async Task ReadFile_Tail_WithSkipEmpty_SkipsBlanksInTailRange()
    {
        // tail + skip_empty:在 tail 范围内跳过空白行
        File.WriteAllText(Path.Combine(_dir, "tse_tail.txt"), "a\n  \nb\n  \nc\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tse_tail.txt", ["tail"] = 3, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("3\tb", output); // tail=3 范围内跳过空白
        Assert.Contains("5\tc", output);
        Assert.DoesNotContain("2\t", output); // 空白行被 skip
        Assert.DoesNotContain("4\t", output);
    }

    [Fact]
    public async Task ReadFile_Tail_WithNoLineNumbers_OutputsRawLines()
    {
        // tail + no_line_numbers:无行号，只输出 tail 范围内的内容
        File.WriteAllText(Path.Combine(_dir, "nl_tail.txt"), "a\nb\nc\nd\ne\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "nl_tail.txt", ["tail"] = 2, ["no_line_numbers"] = true }, ctx, CancellationToken.None);

        Assert.Contains("d" + Environment.NewLine + "e", output);
        Assert.DoesNotContain("3\tc", output); // tail 范围外
    }

    [Fact]
    public async Task ReadFile_Tail_WithMaxLineLength_TruncatesInTailRange()
    {
        // tail + max_line_length:在 tail 范围内截断超长行
        File.WriteAllText(Path.Combine(_dir, "tml_tail.txt"), "short\n" + new string('X', 100) + "\nlast\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tml_tail.txt", ["tail"] = 2, ["max_line_length"] = 20 }, ctx, CancellationToken.None);

        Assert.Contains("…", output); // 超长行被截断
        Assert.Contains("last", output); // 最后一行 intact
    }

    [Fact]
    public async Task ReadFile_Tail_WithRaw_OutputsUnmodifiedContent()
    {
        // tail + raw:无行号，不截断，输出 tail 范围内的原始内容
        File.WriteAllText(Path.Combine(_dir, "raw_tail.txt"), "a\nb\nc\nd\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "raw_tail.txt", ["tail"] = 2, ["raw"] = true }, ctx, CancellationToken.None);

        Assert.Equal("c" + Environment.NewLine + "d", output);
    }

    [Fact]
    public async Task ReadFile_Head_WithSkipEmpty_SkipsBlanksInHeadRange()
    {
        // head + skip_empty:在 head 范围内跳过空白行
        File.WriteAllText(Path.Combine(_dir, "hse.txt"), "  \n  a  \n   \nb\nc\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "hse.txt", ["head"] = 3, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("a", output); // head=3 范围内跳过空白后仍有内容
        Assert.DoesNotContain("1\t", output); // 空白行被 skip
        Assert.DoesNotContain("c", output); // head 范围外
    }

    [Fact]
    public async Task ReadFile_Head_WithNoLineNumbers_OutputsRawLines()
    {
        // head + no_line_numbers:无行号，只输出 head 范围内的内容
        File.WriteAllText(Path.Combine(_dir, "nl_head.txt"), "a\nb\nc\nd\ne\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "nl_head.txt", ["head"] = 2, ["no_line_numbers"] = true }, ctx, CancellationToken.None);

        Assert.Contains("a" + Environment.NewLine + "b", output);
        Assert.DoesNotContain("c", output);
    }

    [Fact]
    public async Task ReadFile_Offset_WithSkipEmpty_SkipsBlanksInOffsetRange()
    {
        // offset + skip_empty:在 offset 范围内跳过空白行
        File.WriteAllText(Path.Combine(_dir, "ose.txt"), "a\n  \nb\n  \nc\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "ose.txt", ["offset"] = 2, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("3\tb", output); // offset=2 范围内跳过空白后第 3 行
        Assert.DoesNotContain("1\ta", output); // offset 范围外
        Assert.DoesNotContain("2\t", output); // 空白行被 skip
    }

    [Fact]
    public async Task ReadFile_Offset_WithNoLineNumbers_OutputsRawLines()
    {
        // offset + no_line_numbers:无行号，只输出 offset 范围内的内容
        File.WriteAllText(Path.Combine(_dir, "nl_off.txt"), "a\nb\nc\nd\ne\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "nl_off.txt", ["offset"] = 3, ["no_line_numbers"] = true }, ctx, CancellationToken.None);

        Assert.Contains("c" + Environment.NewLine + "d" + Environment.NewLine + "e", output);
        Assert.DoesNotContain("a", output);
    }

    [Fact]
    public async Task ReadFile_MaxLineLength_WithHead_TruncatesInHeadRange()
    {
        // max_line_length + head:在 head 范围内截断超长行
        File.WriteAllText(Path.Combine(_dir, "mlh.txt"), "short\n" + new string('X', 100) + "\nlast\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "mlh.txt", ["max_line_length"] = 20, ["head"] = 2 }, ctx, CancellationToken.None);

        Assert.Contains("…", output); // 超长行被截断
        Assert.DoesNotContain("last", output); // head=2 范围外
    }

    [Fact]
    public async Task ReadFile_MaxLineLength_WithTail_TruncatesInTailRange()
    {
        // max_line_length + tail:在 tail 范围内截断超长行
        File.WriteAllText(Path.Combine(_dir, "mlt.txt"), "short\n" + new string('X', 100) + "\nlast\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "mlt.txt", ["max_line_length"] = 20, ["tail"] = 2 }, ctx, CancellationToken.None);

        Assert.Contains("…", output); // 超长行被截断
        Assert.DoesNotContain("short", output); // tail 范围外
    }

    [Fact]
    public async Task ReadFile_MaxLineLength_WithOffset_TruncatesInOffsetRange()
    {
        // max_line_length + offset:在 offset 范围内截断超长行
        File.WriteAllText(Path.Combine(_dir, "mlo.txt"), "short\n" + new string('X', 100) + "\nlast\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "mlo.txt", ["max_line_length"] = 20, ["offset"] = 2 }, ctx, CancellationToken.None);

        Assert.Contains("…", output); // 超长行被截断
        Assert.DoesNotContain("short", output); // offset 范围外
    }

    [Fact]
    public async Task ReadFile_NoEncodingNote_WithHead_SuppressesHintInHeadRange()
    {
        // no_encoding_note + head:编码提示隐藏，只显示 head 范围
        File.WriteAllText(Path.Combine(_dir, "neh.txt"), "a\nb\nc\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "neh.txt", ["no_encoding_note"] = true, ["head"] = 2 }, ctx, CancellationToken.None);

        Assert.Contains("1\ta", output);
        Assert.Contains("2\tb", output);
        Assert.DoesNotContain("（编码", output); // 编码提示隐藏
        Assert.DoesNotContain("c", output); // head 范围外
    }

    [Fact]
    public async Task ReadFile_NoEncodingNote_WithTail_SuppressesHintInTailRange()
    {
        // no_encoding_note + tail:编码提示隐藏，只显示 tail 范围
        File.WriteAllText(Path.Combine(_dir, "net.txt"), "a\nb\nc\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "net.txt", ["no_encoding_note"] = true, ["tail"] = 2 }, ctx, CancellationToken.None);

        Assert.Contains("2\tb", output);
        Assert.Contains("3\tc", output);
        Assert.DoesNotContain("（编码", output); // 编码提示隐藏
        Assert.DoesNotContain("a", output); // tail 范围外
    }

    [Fact]
    public async Task ReadFile_Raw_DisablesMaxLineLength()
    {
        // raw=true:max_line_length 被忽略（raw 不截断）
        File.WriteAllText(Path.Combine(_dir, "rrml.txt"), new string('X', 100) + "\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "rrml.txt", ["raw"] = true, ["max_line_length"] = 10 }, ctx, CancellationToken.None);

        Assert.DoesNotContain("…", output); // raw 不截断
    }

    [Fact]
    public async Task ReadFile_Raw_DisablesNoLineNumbers()
    {
        // raw=true:no_line_numbers 已隐含，但验证输出无行号
        File.WriteAllText(Path.Combine(_dir, "rrnl.txt"), "a\nb\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "rrnl.txt", ["raw"] = true, ["no_line_numbers"] = true }, ctx, CancellationToken.None);

        Assert.DoesNotContain("1\t", output);
        Assert.DoesNotContain("2\t", output);
        Assert.Contains("a" + Environment.NewLine + "b", output);
    }

    [Fact]
    public async Task ReadFile_MaxLineLength_WithSkipEmpty_TruncatesNonBlankLines()
    {
        // max_line_length + skip_empty:跳过空白行，非空白行仍截断
        File.WriteAllText(Path.Combine(_dir, "mlse.txt"), "  \n" + new string('X', 100) + "\nshort\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "mlse.txt", ["max_line_length"] = 20, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("…", output); // 超长行被截断
        Assert.Contains("short", output); // 短行 intact
        Assert.DoesNotContain("1\t", output); // 空白行被 skip
    }

    [Fact]
    public async Task ReadFile_MaxLineLength_WithRaw_NoTruncation()
    {
        // max_line_length + raw:raw 模式不截断，max_line_length 被忽略
        File.WriteAllText(Path.Combine(_dir, "mlr.txt"), new string('X', 100) + "\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "mlr.txt", ["max_line_length"] = 10, ["raw"] = true }, ctx, CancellationToken.None);

        Assert.DoesNotContain("…", output); // raw 不截断
    }

    [Fact]
    public async Task ReadFile_Head_WithMaxLineLength_TruncatesInHeadRange()
    {
        // head + max_line_length:在 head 范围内截断超长行
        File.WriteAllText(Path.Combine(_dir, "hml.txt"), "short\n" + new string('X', 100) + "\nlast\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "hml.txt", ["head"] = 2, ["max_line_length"] = 20 }, ctx, CancellationToken.None);

        Assert.Contains("…", output); // 超长行被截断
        Assert.DoesNotContain("last", output); // head 范围外
    }

    [Fact]
    public async Task ReadFile_Head_WithMaxLineLength_AndSkipEmpty_TruncatesInHeadRange()
    {
        // head + max_line_length + skip_empty:在 head 范围内跳过空白并截断
        File.WriteAllText(Path.Combine(_dir, "hmlse.txt"), "  \n" + new string('X', 100) + "\nshort\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "hmlse.txt", ["head"] = 2, ["max_line_length"] = 20, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("…", output); // 超长行被截断
        Assert.DoesNotContain("short", output); // head 范围外
        Assert.DoesNotContain("1\t", output); // 空白行被 skip
    }

    [Fact]
    public async Task ReadFile_Tail_WithMaxLineLength_AndSkipEmpty_TruncatesInTailRange()
    {
        // tail + max_line_length + skip_empty:在 tail 范围内跳过空白并截断
        File.WriteAllText(Path.Combine(_dir, "tmltse.txt"), "  \n" + new string('X', 100) + "\nshort\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tmltse.txt", ["tail"] = 2, ["max_line_length"] = 20, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("…", output); // 超长行被截断
        Assert.Contains("short", output); // tail 范围内
        Assert.DoesNotContain("1\t", output); // 空白行被 skip
    }

    [Fact]
    public async Task ReadFile_Offset_WithMaxLineLength_TruncatesInOffsetRange()
    {
        // offset + max_line_length:在 offset 范围内截断超长行
        File.WriteAllText(Path.Combine(_dir, "oml.txt"), "short\n" + new string('X', 100) + "\nlast\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "oml.txt", ["offset"] = 2, ["max_line_length"] = 20 }, ctx, CancellationToken.None);

        Assert.Contains("…", output); // 超长行被截断
        Assert.DoesNotContain("short", output); // offset 范围外
    }

    [Fact]
    public async Task ReadFile_Offset_WithMaxLineLength_AndSkipEmpty_TruncatesInOffsetRange()
    {
        // offset + max_line_length + skip_empty:在 offset 范围内跳过空白并截断
        File.WriteAllText(Path.Combine(_dir, "omlse.txt"), "  \n" + new string('X', 100) + "\nshort\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "omlse.txt", ["offset"] = 2, ["max_line_length"] = 20, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("…", output); // 超长行被截断
        Assert.Contains("short", output); // offset 范围内
        Assert.DoesNotContain("1\t", output); // 空白行被 skip
    }

    [Fact]
    public async Task ReadFile_Head_WithTail_AndMaxLineLength_TailTakesPriority()
    {
        // head + tail + max_line_length:tail 优先（head 被忽略），截断生效
        File.WriteAllText(Path.Combine(_dir, "html.txt"), "short\n" + new string('X', 100) + "\nlast\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "html.txt", ["head"] = 2, ["tail"] = 2, ["max_line_length"] = 20 }, ctx, CancellationToken.None);

        Assert.Contains("…", output); // 超长行被截断
        Assert.Contains("last", output); // tail 优先
        Assert.DoesNotContain("short", output); // head 被忽略
    }

    [Fact]
    public async Task ReadFile_Tail_WithHead_AndMaxLineLength_TruncatesInRange()
    {
        // tail + head + max_line_length:tail 优先（head 被忽略），截断生效
        File.WriteAllText(Path.Combine(_dir, "tlhl.txt"), "short\n" + new string('X', 100) + "\nlast\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tlhl.txt", ["tail"] = 2, ["head"] = 1, ["max_line_length"] = 20 }, ctx, CancellationToken.None);

        Assert.Contains("…", output); // 超长行被截断
        Assert.DoesNotContain("short", output); // tail 优先，head 被忽略
    }

    [Fact]
    public async Task ReadFile_Head_WithTail_AndSkipEmpty_SkipsBlanksInRange()
    {
        // head + tail + skip_empty:tail 优先，在范围内跳过空白行
        File.WriteAllText(Path.Combine(_dir, "hts.txt"), "a\n  \nb\n  \nc\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "hts.txt", ["head"] = 3, ["tail"] = 2, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("c", output); // tail=2 范围内跳过空白后只剩 c
        Assert.DoesNotContain("a", output); // head 被忽略
        Assert.DoesNotContain("b", output); // tail 范围外
    }

    [Fact]
    public async Task ReadFile_Tail_WithHead_AndSkipEmpty_SkipsBlanksInRange()
    {
        // tail + head + skip_empty:tail 优先，在范围内跳过空白行
        File.WriteAllText(Path.Combine(_dir, "ths.txt"), "a\n  \nb\n  \nc\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "ths.txt", ["tail"] = 2, ["head"] = 3, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("c", output); // tail=2 范围内跳过空白后只剩 c
        Assert.DoesNotContain("a", output); // head 被忽略
        Assert.DoesNotContain("b", output); // tail 范围外
    }

    [Fact]
    public async Task ReadFile_Head_WithTail_AndNoLineNumbers_OutputsRawLines()
    {
        // head + tail + no_line_numbers:tail 优先，无行号
        File.WriteAllText(Path.Combine(_dir, "htnl.txt"), "a\nb\nc\nd\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "htnl.txt", ["head"] = 3, ["tail"] = 2, ["no_line_numbers"] = true }, ctx, CancellationToken.None);

        Assert.Contains("c" + Environment.NewLine + "d", output); // tail=2 显示最后 2 行
        Assert.DoesNotContain("1\t", output); // 无行号
        Assert.DoesNotContain("a", output); // head 被忽略
    }

    [Fact]
    public async Task ReadFile_Tail_WithHead_AndNoLineNumbers_OutputsRawLines()
    {
        // tail + head + no_line_numbers:tail 优先，无行号
        File.WriteAllText(Path.Combine(_dir, "thnl.txt"), "a\nb\nc\nd\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "thnl.txt", ["tail"] = 2, ["head"] = 1, ["no_line_numbers"] = true }, ctx, CancellationToken.None);

        Assert.Contains("c" + Environment.NewLine + "d", output); // tail=2 显示最后 2 行
        Assert.DoesNotContain("1\t", output); // 无行号
        Assert.DoesNotContain("a", output); // head 被忽略
    }

    [Fact]
    public async Task ReadFile_Head_WithTail_AndRaw_OutputsUnmodifiedContent()
    {
        // head + tail + raw:tail 优先，raw 模式无行号不截断
        File.WriteAllText(Path.Combine(_dir, "htr.txt"), "a\nb\nc\nd\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "htr.txt", ["head"] = 3, ["tail"] = 2, ["raw"] = true }, ctx, CancellationToken.None);

        Assert.Contains("c" + Environment.NewLine + "d", output); // tail=2 显示最后 2 行
        Assert.DoesNotContain("1\t", output); // 无行号
        Assert.DoesNotContain("a", output); // head 被忽略
    }

    [Fact]
    public async Task ReadFile_Tail_WithHead_AndRaw_OutputsUnmodifiedContent()
    {
        // tail + head + raw:tail 优先，raw 模式无行号不截断
        File.WriteAllText(Path.Combine(_dir, "thr.txt"), "a\nb\nc\nd\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "thr.txt", ["tail"] = 2, ["head"] = 1, ["raw"] = true }, ctx, CancellationToken.None);

        Assert.Contains("c" + Environment.NewLine + "d", output); // tail=2 显示最后 2 行
        Assert.DoesNotContain("1\t", output); // 无行号
        Assert.DoesNotContain("a", output); // head 被忽略
    }

    [Fact]
    public async Task ReadFile_Head_WithTail_AndNoEncodingNote_SuppressesHint()
    {
        // head + tail + no_encoding_note:tail 优先，编码提示隐藏
        File.WriteAllText(Path.Combine(_dir, "htenn.txt"), "a\nb\nc\nd\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "htenn.txt", ["head"] = 3, ["tail"] = 2, ["no_encoding_note"] = true }, ctx, CancellationToken.None);

        Assert.Contains("3\tc", output); // tail=2 显示最后 2 行
        Assert.Contains("4\td", output);
        Assert.DoesNotContain("（编码", output); // 编码提示隐藏
        Assert.DoesNotContain("a", output); // head 被忽略
    }

    [Fact]
    public async Task ReadFile_Tail_WithHead_AndNoEncodingNote_SuppressesHint()
    {
        // tail + head + no_encoding_note:tail 优先，编码提示隐藏
        File.WriteAllText(Path.Combine(_dir, "thenn.txt"), "a\nb\nc\nd\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "thenn.txt", ["tail"] = 2, ["head"] = 1, ["no_encoding_note"] = true }, ctx, CancellationToken.None);

        Assert.Contains("3\tc", output); // tail=2 显示最后 2 行
        Assert.Contains("4\td", output);
        Assert.DoesNotContain("（编码", output); // 编码提示隐藏
        Assert.DoesNotContain("a", output); // head 被忽略
    }

    [Fact]
    public async Task ReadFile_Head_WithTrim_TrimsLeadingWhitespace()
    {
        // head + trim:在 head 范围内去掉每行首尾空白
        File.WriteAllText(Path.Combine(_dir, "htt.txt"), "  a  \n  b  \n  c  \n  d  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "htt.txt", ["head"] = 2, ["trim"] = true }, ctx, CancellationToken.None);

        Assert.Contains("1\ta", output); // 首尾空白被 trim
        Assert.Contains("2\tb", output);
        Assert.DoesNotContain("  a  ", output); // 原始带空白内容不应出现
    }

    [Fact]
    public async Task ReadFile_Tail_WithTrim_TrimsLeadingWhitespace()
    {
        // tail + trim:在 tail 范围内去掉每行首尾空白
        File.WriteAllText(Path.Combine(_dir, "ttt.txt"), "  a  \n  b  \n  c  \n  d  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "ttt.txt", ["tail"] = 2, ["trim"] = true }, ctx, CancellationToken.None);

        Assert.Contains("3\tc", output); // 首尾空白被 trim
        Assert.Contains("4\td", output);
        Assert.DoesNotContain("  c  ", output); // 原始带空白内容不应出现
    }

    [Fact]
    public async Task ReadFile_Offset_WithTrim_TrimsLeadingWhitespace()
    {
        // offset + trim:在 offset 范围内去掉每行首尾空白
        File.WriteAllText(Path.Combine(_dir, "ot.txt"), "  a  \n  b  \n  c  \n  d  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "ot.txt", ["offset"] = 2, ["trim"] = true }, ctx, CancellationToken.None);

        Assert.Contains("2\tb", output); // 首尾空白被 trim
        Assert.Contains("3\tc", output);
        Assert.DoesNotContain("  b  ", output); // 原始带空白内容不应出现
    }

    [Fact]
    public async Task ReadFile_SkipEmpty_WithHead_ShowsCorrectRange()
    {
        // skip_empty + head:范围显示应反映实际显示的行（跳过空白行后）
        File.WriteAllText(Path.Combine(_dir, "sehr.txt"), "a\n  \nb\n  \nc\n  \nd\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "sehr.txt", ["head"] = 5, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("已显示 1-5", output); // 范围显示为 1-5（跳过了空白行但行号不变）
        Assert.Contains("1\ta", output);
        Assert.Contains("3\tb", output);
        Assert.Contains("5\tc", output);
        Assert.DoesNotContain("2\t", output); // 第 2 行是空白，被 skip
        Assert.DoesNotContain("4\t", output); // 第 4 行是空白，被 skip
    }

    [Fact]
    public async Task ReadFile_SkipEmpty_WithTail_ShowsCorrectRange()
    {
        // skip_empty + tail:范围显示应反映实际显示的行
        File.WriteAllText(Path.Combine(_dir, "setr.txt"), "a\n  \nb\n  \nc\n  \nd\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "setr.txt", ["tail"] = 3, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("已显示 5-7", output); // 范围显示为 5-7（tail=3 的范围）
        Assert.Contains("5\tc", output);
        Assert.Contains("7\td", output);
        Assert.DoesNotContain("6\t", output); // 第 6 行是空白，被 skip
    }

    [Fact]
    public async Task ReadFile_Raw_WithSkipEmpty_OmitsBlanks()
    {
        // raw + skip_empty:raw 模式仍可跳过空白行
        File.WriteAllText(Path.Combine(_dir, "rse.txt"), "a\n  \nb\n  \nc\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "rse.txt", ["raw"] = true, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("a" + Environment.NewLine + "b" + Environment.NewLine + "c", output); // 空白行被 skip
        Assert.DoesNotContain("  ", output); // 空白行不出现
    }

    [Fact]
    public async Task ReadFile_NoHeader_SuppressesRangeHint()
    {
        // no_header=true:隐藏头部范围提示
        File.WriteAllText(Path.Combine(_dir, "nh.txt"), "a\nb\nc\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "nh.txt", ["no_header"] = true }, ctx, CancellationToken.None);

        Assert.Contains("1\ta", output);
        Assert.Contains("2\tb", output);
        Assert.DoesNotContain("（nh.txt", output); // 头部提示被隐藏
    }

    [Fact]
    public async Task ReadFile_NoHeader_WithRaw_StillNoHeader()
    {
        // no_header=true + raw=true:raw 已隐含 no_header，但验证输出无头部
        File.WriteAllText(Path.Combine(_dir, "nhr.txt"), "a\nb\nc\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "nhr.txt", ["no_header"] = true, ["raw"] = true }, ctx, CancellationToken.None);

        Assert.Contains("a" + Environment.NewLine + "b", output);
        Assert.DoesNotContain("（nhr.txt", output); // 头部提示被隐藏
    }

    [Fact]
    public async Task ReadFile_TailZero_FallsBackToOffsetLimit()
    {
        // tail=0（默认）：行为与之前完全一致（offset/limit 从头读）
        File.WriteAllLines(Path.Combine(_dir, "seq.txt"), Enumerable.Range(1, 20).Select(i => $"L{i}"));
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var result = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "seq.txt", ["offset"] = 2, ["limit"] = 2 },
            ctx, CancellationToken.None);

        Assert.Contains("2\tL2", result);
        Assert.Contains("3\tL3", result);
        Assert.DoesNotContain("L4", result);
    }

    [Fact]
    public async Task WriteFile_MissingContent_Throws()
    {
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);
        var args = new JsonObject { ["path"] = "a.txt" };

        var ex = await Assert.ThrowsAsync<ToolException>(
            () => tool.ExecuteAsync(args, ctx, CancellationToken.None));
        Assert.Contains("content", ex.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "a.txt"))); // 不应静默写空文件
    }

    [Fact]
    public async Task WriteFile_EmptyContent_IsAllowed()
    {
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);
        var args = new JsonObject { ["path"] = "b.txt", ["content"] = "" };

        await tool.ExecuteAsync(args, ctx, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(_dir, "b.txt"))); // 显式空串是合法写入
    }

    [Fact]
    public async Task WriteFile_ResultIncludesByteAndLineCount()
    {
        // 结果含字节数与行数，模型可据此与预期对照
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);
        var args = new JsonObject { ["path"] = "c.txt", ["content"] = "line1\nline2\nline3" };

        var result = await tool.ExecuteAsync(args, ctx, CancellationToken.None);

        Assert.Contains("字节", result);
        Assert.Contains("（3 行）", result); // 3 行非空内容
    }

    [Fact]
    public async Task WriteFile_EmptyResultReportsZeroLines()
    {
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);
        var args = new JsonObject { ["path"] = "e.txt", ["content"] = "" };

        var result = await tool.ExecuteAsync(args, ctx, CancellationToken.None);

        Assert.Contains("（0 行）", result);
    }

    [Fact]
    public async Task ReadFile_DefaultsToLineNumbers()
    {
        var path = Path.Combine(_dir, "r1.txt");
        File.WriteAllText(path, "第一行\nsecond");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(new JsonObject { ["path"] = "r1.txt" }, ctx, CancellationToken.None);
        Assert.Contains("1\t第一行", output);
        Assert.Contains("2\tsecond", output);
    }

    [Fact]
    public async Task ReadFile_SingleLineNoTrailingNewline_ReadsAsOneLine()
    {
        // 文件只有一行且无结尾换行：不应出现幽灵空行或行号错位
        File.WriteAllText(Path.Combine(_dir, "single.txt"), "only line");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(new JsonObject { ["path"] = "single.txt" }, ctx, CancellationToken.None);
        Assert.Contains("1\tonly line", output);
        Assert.False(output.Contains("2\t"), "不应有第 2 行");
    }

    [Fact]
    public async Task ReadFile_EmptyFile_ReturnsFriendlyMessage()
    {
        // 0 字节文件不应报错或输出幽灵空行，应明确提示「文件为空」
        File.WriteAllText(Path.Combine(_dir, "empty.txt"), "");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(new JsonObject { ["path"] = "empty.txt" }, ctx, CancellationToken.None);
        Assert.Contains("为空", output);
    }

    [Fact]
    public async Task ReadFile_NoLineNumbers_OutputsRawText()
    {
        var path = Path.Combine(_dir, "r2.txt");
        File.WriteAllText(path, "第一行\nsecond");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "r2.txt", ["no_line_numbers"] = true }, ctx, CancellationToken.None);
        Assert.Contains("第一行", output);
        Assert.Contains("second", output);
        Assert.DoesNotContain("1\t", output); // 不应出现行号前缀
    }

    [Fact]
    public async Task ReadFile_FullRead_ShowsTotalLineCountHeader()
    {
        // 回归：即便未截断，也应告知总行数，便于模型判断是否需要 offset 分段续读
        var path = Path.Combine(_dir, "r3.txt");
        File.WriteAllText(path, "a\nb\nc\nd");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(new JsonObject { ["path"] = "r3.txt" }, ctx, CancellationToken.None);
        Assert.Contains("共 4 行", output);
        Assert.DoesNotContain("已显示", output); // 未截断时不标「已显示 x-y」
    }

    [Fact]
    public async Task ReadFile_BinaryFile_ThrowsInsteadOfGarbage()
    {
        // 回归：二进制文件（含 NUL 字节）不应输出乱码行，应明确报错
        var path = Path.Combine(_dir, "bin.dat");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x41, 0x00]);
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(
            () => tool.ExecuteAsync(new JsonObject { ["path"] = "bin.dat" }, ctx, CancellationToken.None));
        Assert.Contains("二进制", ex.Message);
    }

    [Fact]
    public async Task ReadFile_CrlfLineEndings_DoNotPolluteLineContent()
    {
        var path = Path.Combine(_dir, "r3.txt");
        File.WriteAllText(path, "aaa\r\nbbb\r\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(new JsonObject { ["path"] = "r3.txt" }, ctx, CancellationToken.None);
        Assert.Contains("1\taaa", output);
        Assert.Contains("2\tbbb", output);
        Assert.DoesNotContain("3\t", output); // 末尾换行不应产生幽灵空行
        Assert.DoesNotContain("bbb\r", output); // 行尾 \r 不应残留在内容里
    }

    [Fact]
    public async Task ReadFile_StringNumberParams_AreParsedTolerantly()
    {
        // 模型常把数字参数序列化为字符串（"2" / "true"），应正确解析而非回退默认值
        var path = Path.Combine(_dir, "r4.txt");
        File.WriteAllText(path, "line1\nline2\nline3");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "r4.txt", ["offset"] = "2", ["limit"] = "1", ["no_line_numbers"] = "true" },
            ctx, CancellationToken.None);
        Assert.Contains("line2", output); // offset=2, limit=1 → 只有第二行
        Assert.DoesNotContain("line1", output);
        Assert.DoesNotContain("line3", output);
    }

    [Fact]
    public async Task ReadFile_Directory_ReturnsHelpfulHint()
    {
        // 回归：对目录调用 read_file 应提示用 list_directory，而非误报"文件不存在"
        var sub = Path.Combine(_dir, "subdir");
        Directory.CreateDirectory(sub);
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(
            () => tool.ExecuteAsync(new JsonObject { ["path"] = "subdir" }, ctx, CancellationToken.None));
        Assert.Contains("list_directory", ex.Message);
        Assert.DoesNotContain("文件不存在", ex.Message);
    }

    [Fact]
    public async Task WriteFile_DirectoryPath_ThrowsHint()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "adir"));
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(
            () => tool.ExecuteAsync(new JsonObject { ["path"] = "adir", ["content"] = "x" }, ctx, CancellationToken.None));
        Assert.Contains("是目录", ex.Message);
    }

    [Fact]
    public async Task EditFile_DirectoryPath_ThrowsHint()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "edir"));
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(
            () => tool.ExecuteAsync(
                new JsonObject { ["path"] = "edir", ["old_string"] = "a", ["new_string"] = "b" },
                ctx, CancellationToken.None));
        Assert.Contains("是目录", ex.Message);
    }

    [Fact]
    public async Task ListDirectory_FilePath_ThrowsHint()
    {
        File.WriteAllText(Path.Combine(_dir, "afile.txt"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(
            () => tool.ExecuteAsync(new JsonObject { ["path"] = "afile.txt" }, ctx, CancellationToken.None));
        Assert.Contains("read_file", ex.Message);
    }

    [Fact]
    public async Task ListDirectory_ListsEntries_SkipsBuildDirs()
    {
        // 目录条目应列出；bin/obj/node_modules 等构建目录应跳过
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        Directory.CreateDirectory(Path.Combine(_dir, "node_modules"));
        File.WriteAllText(Path.Combine(_dir, "README.md"), "x");
        File.WriteAllText(Path.Combine(_dir, "src", "Program.cs"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(new JsonObject(), ctx, CancellationToken.None);

        Assert.Contains("src/", output);
        Assert.Contains("README.md", output);
        Assert.Contains("Program.cs", output);
        Assert.DoesNotContain("bin/", output);
        Assert.DoesNotContain("node_modules", output);
    }

    [Fact]
    public async Task ListDirectory_AppendsCountSummary()
    {
        // 统计摘要：目录数与文件数一目了然（截断时提示可能未列全）
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "sub", "c.cs"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(new JsonObject(), ctx, CancellationToken.None);

        Assert.Contains("共 1 个目录、3 个文件）", output);   // 未截断：无「未列全」字样
        Assert.DoesNotContain("未列全", output);
    }

    [Fact]
    public async Task ListDirectory_Ignore_SkipsNamedDirs()
    {
        // ignore 跳过指定目录名（大小写不敏感），且不计入统计
        Directory.CreateDirectory(Path.Combine(_dir, "node_modules"));
        File.WriteAllText(Path.Combine(_dir, "node_modules", "lib.js"), "x");
        File.WriteAllText(Path.Combine(_dir, "keep.txt"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["ignore"] = new JsonArray("node_modules", "VENDOR") }, ctx, CancellationToken.None);

        Assert.DoesNotContain("node_modules", output);
        Assert.Contains("keep.txt", output);
    }

    [Fact]
    public async Task ListDirectory_Ignore_IsCaseInsensitive()
    {
        // ignore 列表区分大小写不应影响跳过行为：NODE_MODULES / Node_Modules 都应命中
        Directory.CreateDirectory(Path.Combine(_dir, "NODE_MODULES"));
        File.WriteAllText(Path.Combine(_dir, "NODE_MODULES", "pkg"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["ignore"] = new JsonArray("node_modules") }, ctx, CancellationToken.None);

        Assert.DoesNotContain("NODE_MODULES", output);
    }

    [Fact]
    public async Task ListDirectory_IgnoreRecursesIntoSkippedDir()
    {
        // 被 ignore 的目录及其子内容都不展示（彻底跳过，而非仅折叠一层）
        Directory.CreateDirectory(Path.Combine(_dir, "skip", "inner"));
        File.WriteAllText(Path.Combine(_dir, "skip", "inner", "x.txt"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["ignore"] = new JsonArray("skip") }, ctx, CancellationToken.None);

        Assert.DoesNotContain("skip", output);
        Assert.DoesNotContain("inner", output);
        Assert.DoesNotContain("x.txt", output);
    }

    [Fact]
    public async Task ListDirectory_OnlySkippedDirs_ReportsEmpty()
    {
        // 目录本身存在，但唯一子目录全是 SkipDirs（如 bin/）时，应友好提示为空/全跳过
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(new JsonObject { ["path"] = "." }, ctx, CancellationToken.None);

        Assert.Contains("全部被跳过", output);
    }

    [Fact]
    public async Task ListDirectory_DepthLimit_ControlsRecursion()
    {
        // depth=0 只列直接子项，不深入任何子目录
        Directory.CreateDirectory(Path.Combine(_dir, "a", "b", "c"));
        File.WriteAllText(Path.Combine(_dir, "a", "top.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "a", "b", "deep.txt"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "a", ["depth"] = 0 }, ctx, CancellationToken.None);

        Assert.Contains("top.txt", output);
        Assert.Contains("b/", output);
        Assert.DoesNotContain("deep.txt", output); // 深度 0 不进入 b
        Assert.DoesNotContain("c/", output);       // 也不显示 b 的子目录
    }

    [Fact]
    public async Task ListDirectory_DepthClamp_HandlesOutOfRange()
    {
        // depth 越界应被收敛：负数→0（只列根子项），超过 5→5（足够深，能显示多层嵌套）
        Directory.CreateDirectory(Path.Combine(_dir, "a", "b", "c", "d"));
        File.WriteAllText(Path.Combine(_dir, "a", "top.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "a", "b", "c", "d", "deep.txt"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var neg = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "a", ["depth"] = -5 }, ctx, CancellationToken.None);
        Assert.Contains("top.txt", neg);
        Assert.Contains("b/", neg);
        Assert.DoesNotContain("deep.txt", neg); // 收敛到 0，不递归

        var big = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "a", ["depth"] = 99 }, ctx, CancellationToken.None);
        Assert.Contains("deep.txt", big); // 收敛到 5，足够深
        Assert.Contains("d/", big);
    }

    [Fact]
    public async Task ListDirectory_FlatDirWithManyFiles_RespectsCap()
    {
        // 回归：cap 曾只在 Walk 入口检查，平铺目录里 foreach 会把 cap 之后的文件行全部输出
        for (int i = 0; i < 900; i++)
            File.WriteAllText(Path.Combine(_dir, $"f{i:000}.txt"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var outText = await tool.ExecuteAsync(new JsonObject { ["path"] = "." }, ctx, CancellationToken.None);

        var lines = outText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("已截断", outText);
        Assert.True(lines.Length <= 803, $"输出行数 {lines.Length} 应被 cap 截断在 ~800 行");
        Assert.Contains("f000.txt", outText); // 头部条目保留
        Assert.DoesNotContain("f899.txt", outText); // 超出 cap 的尾部条目被截掉
    }

    [Fact]
    public async Task ListDirectory_MaxItems_MinimumOneItem()
    {
        // max_items 下限为 1：传 0 或负值时回退到 1（至少能看到一个条目以确认目录存在）
        for (int i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(_dir, $"min{i:00}.txt"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var outText = await tool.ExecuteAsync(new JsonObject { ["path"] = ".", ["max_items"] = 0 }, ctx, CancellationToken.None);

        Assert.Contains("min00.txt", outText); // 至少第一个
        Assert.DoesNotContain("min09.txt", outText); // 超出 max_items 被截断
    }

    [Fact]
    public async Task ListDirectory_FilesOnly_WithIgnore_SkipsMatchingDirs()
    {
        // files_only + ignore:目录被跳过（不递归）；ignore 对文件无效（仅作用于目录名）
        Directory.CreateDirectory(Path.Combine(_dir, "skipdir"));
        File.WriteAllText(Path.Combine(_dir, "skipdir", "inner.txt"), "y");
        File.WriteAllText(Path.Combine(_dir, "root.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "skip.me"), "z");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var outText = await tool.ExecuteAsync(
            new JsonObject { ["path"] = ".", ["files_only"] = true, ["ignore"] = new JsonArray("skipdir") }, ctx, CancellationToken.None);

        Assert.Contains("root.txt", outText);
        Assert.Contains("skip.me", outText); // ignore 仅作用于目录，不影响文件
        Assert.DoesNotContain("inner.txt", outText); // 目录被跳过
        Assert.DoesNotContain("skipdir/", outText);
    }

    [Fact]
    public async Task ListDirectory_FilesOnly_OmitsDirectories()
    {
        // files_only=true：只列文件，目录及其子项全部跳过
        Directory.CreateDirectory(Path.Combine(_dir, "subdir"));
        File.WriteAllText(Path.Combine(_dir, "root.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "subdir", "inner.txt"), "y");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var outText = await tool.ExecuteAsync(
            new JsonObject { ["path"] = ".", ["files_only"] = true, ["depth"] = 2 }, ctx, CancellationToken.None);

        Assert.Contains("root.txt", outText);
        Assert.DoesNotContain("subdir/", outText);
        Assert.DoesNotContain("inner.txt", outText); // 子目录被跳过，深度内也不递归
    }

    [Fact]
    public async Task ListDirectory_FilesOnly_RespectsMaxItems()
    {
        // files_only + max_items: 只列文件，且条目不超过 max_items
        for (int i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(_dir, $"f{i:00}.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_dir, "skipdir"));
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var outText = await tool.ExecuteAsync(
            new JsonObject { ["path"] = ".", ["files_only"] = true, ["max_items"] = 3 }, ctx, CancellationToken.None);

        Assert.Contains("f00.txt", outText);
        Assert.DoesNotContain("f03.txt", outText); // 被 max_items=3 截断
        Assert.DoesNotContain("skipdir/", outText); // 目录被跳过
    }

    [Fact]
    public async Task ListDirectory_FilesOnly_DeepNested_SkipsAllDirectories()
    {
        // files_only + depth=3: 深层嵌套目录中的文件也不应出现（目录被完全跳过）
        Directory.CreateDirectory(Path.Combine(_dir, "a", "b", "c"));
        File.WriteAllText(Path.Combine(_dir, "a", "b", "c", "deep.txt"), "z");
        File.WriteAllText(Path.Combine(_dir, "root.txt"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var outText = await tool.ExecuteAsync(
            new JsonObject { ["path"] = ".", ["files_only"] = true, ["depth"] = 3 }, ctx, CancellationToken.None);

        Assert.Contains("root.txt", outText); // 根目录文件
        Assert.DoesNotContain("deep.txt", outText); // 深层文件被跳过（因为目录被跳过）
        Assert.DoesNotContain("a/", outText); // 目录名也不出现
    }

    [Fact]
    public async Task ListDirectory_DirsOnly_RespectsMaxItems()
    {
        // dirs_only + max_items:只列目录，且条目不超过 max_items
        for (int i = 0; i < 5; i++)
            Directory.CreateDirectory(Path.Combine(_dir, $"d{i:00}"));
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var outText = await tool.ExecuteAsync(
            new JsonObject { ["path"] = ".", ["dirs_only"] = true, ["max_items"] = 3 }, ctx, CancellationToken.None);

        Assert.Contains("d00/", outText);
        Assert.DoesNotContain("d03/", outText); // 被 max_items=3 截断
    }

    [Fact]
    public async Task ListDirectory_EmptyDir_ReportsEmpty()
    {
        // 空目录：返回空提示
        Directory.CreateDirectory(Path.Combine(_dir, "emptydir"));
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var outText = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "emptydir" }, ctx, CancellationToken.None);

        Assert.Contains("为空", outText); // 空目录提示
    }

    [Fact]
    public async Task ListDirectory_DirsOnly_WithIgnore_SkipsMatchingDirs()
    {
        // dirs_only + ignore: ignore 只作用于目录名，文件本来就不显示
        Directory.CreateDirectory(Path.Combine(_dir, "skipdir"));
        Directory.CreateDirectory(Path.Combine(_dir, "keepdir"));
        File.WriteAllText(Path.Combine(_dir, "keepdir", "f.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "root.txt"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var outText = await tool.ExecuteAsync(
            new JsonObject { ["path"] = ".", ["dirs_only"] = true, ["ignore"] = new JsonArray("skipdir") }, ctx, CancellationToken.None);

        Assert.Contains("keepdir/", outText);
        Assert.DoesNotContain("skipdir/", outText); // 被 ignore 跳过
        Assert.DoesNotContain("root.txt", outText); // 文件不显示
    }

    [Fact]
    public async Task ListDirectory_DirsOnly_OmitsFiles()
    {
        // dirs_only=true:只列目录，文件全部跳过
        Directory.CreateDirectory(Path.Combine(_dir, "subdir"));
        File.WriteAllText(Path.Combine(_dir, "root.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "subdir", "inner.txt"), "y");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var outText = await tool.ExecuteAsync(
            new JsonObject { ["path"] = ".", ["dirs_only"] = true, ["depth"] = 2 }, ctx, CancellationToken.None);

        Assert.Contains("subdir/", outText);
        Assert.DoesNotContain("root.txt", outText);
        Assert.DoesNotContain("inner.txt", outText); // 文件被跳过
    }

    [Fact]
    public async Task ListDirectory_FilesOnlyAndDirsOnly_ReturnsEmpty()
    {
        // files_only=true 且 dirs_only=true:矛盾条件，结果为空
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_dir, "d"));
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var outText = await tool.ExecuteAsync(
            new JsonObject { ["path"] = ".", ["files_only"] = true, ["dirs_only"] = true }, ctx, CancellationToken.None);

        Assert.Equal("(目录为空或全部被跳过: .)", outText);
    }

    [Fact]
    public async Task ListDirectory_MaxItems_OverridesCap()
    {
        // max_items 可把默认 800 的上限放大或缩小：默认不传时为 800；传参可任意调整
        for (int i = 0; i < 200; i++)
            File.WriteAllText(Path.Combine(_dir, $"m{i:000}.txt"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var outText = await tool.ExecuteAsync(new JsonObject { ["path"] = ".", ["max_items"] = 30 }, ctx, CancellationToken.None);

        Assert.Contains("已截断", outText); // 自定义上限后仍会被截断
        Assert.DoesNotContain("m099.txt", outText); // 超出 max_items 的尾部被截掉
        Assert.Contains("m000.txt", outText); // 头部保留
    }

    [Fact]
    public async Task ListDirectory_EmptyDir_ReportsHelpfully()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "empty"));
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "empty" }, ctx, CancellationToken.None);

        Assert.Contains("空", output);
    }

    [Fact]
    public async Task ListDirectory_DepthZero_FilesOnly_OnlyRootFiles()
    {
        // depth=0 + files_only:只列根目录文件，不递归
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "root.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "sub", "inner.txt"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = ".", ["depth"] = 0, ["files_only"] = true }, ctx, CancellationToken.None);

        Assert.Contains("root.txt", output);
        Assert.DoesNotContain("inner.txt", output); // 不递归
    }

    [Fact]
    public async Task ListDirectory_DepthZero_DirsOnly_OnlyRootDirs()
    {
        // depth=0 + dirs_only:只列根目录目录，不递归
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        Directory.CreateDirectory(Path.Combine(_dir, "sub", "inner"));
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = ".", ["depth"] = 0, ["dirs_only"] = true }, ctx, CancellationToken.None);

        Assert.Contains("sub/", output);
        Assert.DoesNotContain("inner/", output); // 不递归
    }

    [Fact]
    public async Task ListDirectory_Entries_AreSorted()
    {
        // 输出确定性：目录组与文件组各自按名称排序（目录先于文件）
        Directory.CreateDirectory(Path.Combine(_dir, "zeta"));
        Directory.CreateDirectory(Path.Combine(_dir, "alpha"));
        File.WriteAllText(Path.Combine(_dir, "mid.txt"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(new JsonObject(), ctx, CancellationToken.None);

        var idxAlpha = output.IndexOf("alpha/", StringComparison.Ordinal);
        var idxZeta = output.IndexOf("zeta/", StringComparison.Ordinal);
        var idxMid = output.IndexOf("mid.txt", StringComparison.Ordinal);
        Assert.True(idxAlpha >= 0 && idxZeta > idxAlpha, $"目录组应按字母序排列:\n{output}");
        Assert.True(idxMid > idxZeta, $"文件组应列在目录之后:\n{output}");
    }

    [Fact]
    public async Task ListDirectory_SortByName_IsAlphabetical()
    {
        // sort_by=name:目录和文件均按名称字母序排列
        Directory.CreateDirectory(Path.Combine(_dir, "zeta"));
        Directory.CreateDirectory(Path.Combine(_dir, "alpha"));
        File.WriteAllText(Path.Combine(_dir, "mid.txt"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["sort_by"] = "name" }, ctx, CancellationToken.None);

        var idxAlpha = output.IndexOf("alpha/", StringComparison.Ordinal);
        var idxZeta = output.IndexOf("zeta/", StringComparison.Ordinal);
        Assert.True(idxAlpha >= 0 && idxZeta > idxAlpha, $"sort_by=name 应按字母序:\n{output}");
    }

    [Fact]
    public async Task ListDirectory_SortBySize_LargestFirst()
    {
        // sort_by=size:文件按大小降序排列（大文件在前）
        File.WriteAllText(Path.Combine(_dir, "small.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "large.txt"), new string('a', 1000));
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["sort_by"] = "size", ["files_only"] = true }, ctx, CancellationToken.None);

        var idxLarge = output.IndexOf("large.txt", StringComparison.Ordinal);
        var idxSmall = output.IndexOf("small.txt", StringComparison.Ordinal);
        Assert.True(idxLarge >= 0 && idxSmall > idxLarge, $"sort_by=size 应大文件在前:\n{output}");
    }

    [Fact]
    public async Task ListDirectory_SortByModified_NewestFirst()
    {
        // sort_by=modified:按修改时间降序（最新修改在前）
        File.WriteAllText(Path.Combine(_dir, "old.txt"), "x");
        Thread.Sleep(50); // 确保时间戳不同
        File.WriteAllText(Path.Combine(_dir, "new.txt"), "y");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["sort_by"] = "modified", ["files_only"] = true }, ctx, CancellationToken.None);

        var idxNew = output.IndexOf("new.txt", StringComparison.Ordinal);
        var idxOld = output.IndexOf("old.txt", StringComparison.Ordinal);
        Assert.True(idxNew >= 0 && idxOld > idxNew, $"sort_by=modified 应最新在前:\n{output}");
    }

    [Fact]
    public async Task WriteFile_NumericContent_IsCoercedToString()
    {
        // 回归：模型偶尔把字符串参数序列化为数字，GetString 应容错转换而非抛 InvalidOperationException
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "n.txt", ["content"] = 123 }, ctx, CancellationToken.None);
        Assert.Equal("123", File.ReadAllText(Path.Combine(_dir, "n.txt")));
    }

    [Fact]
    public async Task ReadFile_OffsetBeyondEnd_ReportsRange()
    {
        // 回归：offset 超出文件行数时曾显示错误范围（如"已显示 11-10"），应友好说明
        var path = Path.Combine(_dir, "r5.txt");
        File.WriteAllText(path, "a\nb\nc");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "r5.txt", ["offset"] = 99 }, ctx, CancellationToken.None);
        Assert.Contains("共 3 行", output);
        Assert.Contains("超出范围", output);
        Assert.DoesNotContain("99-99", output); // 不应出现倒退的行号区间
    }

    [Fact]
    public async Task ReadFile_VeryLongLine_IsTruncated()
    {
        // 回归：压缩 JSON / base64 等超长行曾整行输出撑爆上下文，现应按 2000 字符/行截断
        var path = Path.Combine(_dir, "long.txt");
        File.WriteAllText(path, new string('x', 5000) + "\nshort");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(new JsonObject { ["path"] = "long.txt" }, ctx, CancellationToken.None);
        Assert.Contains("…", output); // 有截断标记
        Assert.True(output.Length < 2500, $"超长行应被截断（当前输出 {output.Length} 字符）");
        Assert.Contains("short", output); // 后续正常行不受影响
    }

    [Fact]
    public async Task ReadFile_MaxLineLength_CustomCap_RespectsOverride()
    {
        // max_line_length: 可把截断阈值从默认 2000 调到更大（或 0 表示不截断），
        // 方便模型读取 base64 / 压缩 JSON 等超长行。
        var path = Path.Combine(_dir, "long.txt");
        File.WriteAllText(path, new string('A', 8000) + "\nshort\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(new JsonObject { ["path"] = "long.txt", ["max_line_length"] = 50 }, ctx, CancellationToken.None);

        Assert.Contains("…", output); // 有截断标记
        Assert.True(output.Length < 200, $"自定义 max_line_length=50 时应更早截断（当前输出 {output.Length} 字符）");
        Assert.Contains("short", output);
    }

    [Fact]
    public async Task ReadFile_MaxLineLength_Zero_DisablesTruncation()
    {
        // max_line_length=0：不截断超长行（用于模型需要完整取回极长行的场景）
        var path = Path.Combine(_dir, "long.txt");
        File.WriteAllText(path, new string('Z', 8000) + "\nshort\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(new JsonObject { ["path"] = "long.txt", ["max_line_length"] = 0 }, ctx, CancellationToken.None);

        Assert.DoesNotContain("…", output); // 不截断时不应有省略标记
        Assert.Contains(new string('Z', 8000), output); // 完整超长行可回
        Assert.Contains("short", output);
    }

    [Fact]
    public async Task WriteFile_CreateDirsTrue_CreatesMissingParents()
    {
        // 默认 create_dirs=true：写入深层路径时应自动创建全部缺失父目录
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "deep/nested/dir/file.txt", ["content"] = "hello" }, ctx, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_dir, "deep", "nested", "dir", "file.txt")));
        Assert.Contains("deep/nested/dir/file.txt", output);
    }

    [Fact]
    public async Task WriteFile_PreservesCrlfContent()
    {
        // 内容含 \r\n 时应原样写入（Windows 工程保持 CRLF，不强行转 LF）
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "crlf.txt", ["content"] = "line1\r\nline2\r\n" }, ctx, CancellationToken.None);

        Assert.Equal("line1\r\nline2\r\n", File.ReadAllText(Path.Combine(_dir, "crlf.txt")));
    }

    [Fact]
    public async Task WriteFile_CreateDirsFalseString_IsRespected()
    {
        // 回归：create_dirs 默认 true，模型传字符串 "0" 时应解析为 false（不自动建目录），
        // 而非回退默认值 true 导致父目录被静默创建。
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);
        var parent = Path.Combine(_dir, "not-created");

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(
                new JsonObject { ["path"] = "not-created/sub/f.txt", ["content"] = "x", ["create_dirs"] = "0" },
                ctx, CancellationToken.None));
        Assert.Contains("父目录不存在", ex.Message); // 清晰错误，而非笼统的「写入失败」
        Assert.False(Directory.Exists(parent)); // 父目录不应被自动创建
    }

    [Fact]
    public async Task WriteFile_FailedWrite_DoesNotPolluteUndoStack()
    {
        // 回归：写入失败时撤销栈不应残留条目（否则 /undo 会撤销一个从未生效的写入）
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(
                new JsonObject { ["path"] = "missing-dir/f.txt", ["content"] = "x", ["create_dirs"] = false },
                ctx, CancellationToken.None));

        Assert.Equal(0, ctx.Undo.Count); // 失败写入不入撤销栈
    }

    [Fact]
    public async Task EditFile_ReplaceAll_ReplacesEveryOccurrence()
    {
        File.WriteAllText(Path.Combine(_dir, "ra.txt"), "foo foo bar foo");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "ra.txt", ["old_string"] = "foo", ["new_string"] = "baz", ["replace_all"] = true },
            ctx, CancellationToken.None);

        Assert.Equal("baz baz bar baz", File.ReadAllText(Path.Combine(_dir, "ra.txt")));
        Assert.Equal(1, ctx.Undo.Count); // 一次编辑只记录一条撤销
    }

    [Fact]
    public async Task EditFile_AmbiguousMatch_WithoutReplaceAll_Throws()
    {
        // old_string 出现多次且未指定 replace_all 时应报错，提示扩大上下文或设 replace_all
        File.WriteAllText(Path.Combine(_dir, "amb.txt"), "x y x");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(
                new JsonObject { ["path"] = "amb.txt", ["old_string"] = "x", ["new_string"] = "z" },
                ctx, CancellationToken.None));
        Assert.Contains("出现 2 次", ex.Message);
        Assert.Equal("x y x", File.ReadAllText(Path.Combine(_dir, "amb.txt"))); // 文件未被改动
        Assert.Equal(0, ctx.Undo.Count); // 失败编辑不入撤销栈
    }

    [Fact]
    public async Task EditFile_ReplaceAll_NoMatch_Throws()
    {
        // 回归：replace_all 未命中时曾静默写回原文件并报「已替换 0 处」，
        // 还往撤销栈塞了无效条目；应与单次替换一样明确报错
        File.WriteAllText(Path.Combine(_dir, "rano.txt"), "aaa bbb");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(
                new JsonObject { ["path"] = "rano.txt", ["old_string"] = "zzz", ["new_string"] = "y", ["replace_all"] = true },
                ctx, CancellationToken.None));
        Assert.Contains("未找到 old_string", ex.Message);
        Assert.Equal("aaa bbb", File.ReadAllText(Path.Combine(_dir, "rano.txt"))); // 文件未被改动
        Assert.Equal(0, ctx.Undo.Count); // 失败不入撤销栈
    }

    [Fact]
    public async Task ReadFile_OffsetBeyondEnd_ReturnsEmpty()
    {
        // offset 超过文件行数时应友好提示而非崩溃
        File.WriteAllText(Path.Combine(_dir, "short.txt"), "only one line");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "short.txt", ["offset"] = 999 }, ctx, CancellationToken.None);
        Assert.Contains("超出范围", output); // 提示超出范围
    }

    [Fact]
    public async Task ReadFile_OffsetZero_ClampsToOne()
    {
        // offset=0 或负数应收敛为 1（第 1 行）
        File.WriteAllText(Path.Combine(_dir, "clamp.txt"), "a\nb\nc");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "clamp.txt", ["offset"] = 0 }, ctx, CancellationToken.None);
        Assert.Contains("1\ta", output);
    }

    [Fact]
    public async Task ReadFile_HeadZero_FallsBackToDefaultLimit()
    {
        // head=0 视为未指定：回退到默认 limit（300 行），而非报错或空结果
        File.WriteAllText(Path.Combine(_dir, "hz.txt"), "line1\nline2\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "hz.txt", ["head"] = 0 }, ctx, CancellationToken.None);

        Assert.Contains("line1", output);
        Assert.Contains("line2", output); // 默认 limit 足够覆盖全部 2 行
    }

    [Fact]
    public async Task ReadFile_LimitTooLarge_ClampsTo5000()
    {
        // limit 超过 5000 应收敛到 5000（最多输出 5000 行），不崩溃
        var content = string.Join('\n', Enumerable.Range(1, 6000).Select(i => $"line{i}"));
        File.WriteAllText(Path.Combine(_dir, "big.txt"), content);
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "big.txt", ["limit"] = 99999 }, ctx, CancellationToken.None);
        Assert.Contains("line5000", output); // 前 5000 行在内
        Assert.DoesNotContain("line6000", output); // 超出 limit 的行不输出
    }

    [Fact]
    public async Task ReadFile_LimitZero_ReadsOneLine()
    {
        // limit=0 被 clamp 到 1：返回第 1 行而非空结果
        File.WriteAllText(Path.Combine(_dir, "lz.txt"), "alpha\nbeta\ngamma\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "lz.txt", ["limit"] = 0 }, ctx, CancellationToken.None);

        Assert.Contains("1\talpha", output);
        Assert.DoesNotContain("beta", output);
    }

    [Fact]
    public async Task ReadFile_Head_ReadsFirstNLines()
    {
        // head 是 limit 的便捷写法：读开头 N 行（tail 未给时优先）
        File.WriteAllText(Path.Combine(_dir, "h.txt"), string.Join('\n', Enumerable.Range(1, 100).Select(i => $"row{i}")));
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "h.txt", ["head"] = 5 }, ctx, CancellationToken.None);
        Assert.Contains("row1", output);
        Assert.Contains("row5", output);
        Assert.DoesNotContain("row6", output);
    }

    [Fact]
    public async Task ReadFile_HeadIgnoredWhenTailGiven()
    {
        // tail 优先于 head：同时给 head 和 tail 时读末尾 N 行
        File.WriteAllText(Path.Combine(_dir, "ht.txt"), string.Join('\n', Enumerable.Range(1, 100).Select(i => $"row{i}")));
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "ht.txt", ["head"] = 5, ["tail"] = 3 }, ctx, CancellationToken.None);
        Assert.Contains("row98", output);
        Assert.Contains("row100", output);
        Assert.DoesNotContain("row50", output); // 中部行不在末尾 N 行内，证明 head 被忽略
    }

    [Fact]
    public async Task EditFile_MissingOldString_ThrowsHelpfulError()
    {
        File.WriteAllText(Path.Combine(_dir, "nomatch.txt"), "hello world");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(
                new JsonObject { ["path"] = "nomatch.txt", ["old_string"] = "zzz", ["new_string"] = "x" },
                ctx, CancellationToken.None));
        Assert.Contains("未找到 old_string", ex.Message);
        // 内容毫无相似之处：不给出空白差异提示（避免误导）
        Assert.DoesNotContain("提示", ex.Message);
        Assert.Equal(0, ctx.Undo.Count); // 失败编辑不入撤销栈
    }

    [Fact]
    public async Task EditFile_WhitespaceOnlyMismatch_HintsAtSimilarContent()
    {
        // 回归：模型常把缩进抄错（如 8 空格当 4 空格），逐字匹配失败后只能盲试。
        // 空白归一化能命中 → 错误信息应直接指出「只差空白」，引导从 read_file 重新复制
        File.WriteAllText(Path.Combine(_dir, "indented.cs"), "void F()\n{\n    return;\n}\n");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(
                new JsonObject
                {
                    ["path"] = "indented.cs",
                    ["old_string"] = "void F()\n{\n        return;\n}", // 缩进不同
                    ["new_string"] = "void G()\n{\n        return;\n}",
                },
                ctx, CancellationToken.None));
        Assert.Contains("未找到 old_string", ex.Message);
        Assert.Contains("仅空白/缩进差异", ex.Message);
        Assert.Equal(0, ctx.Undo.Count);
    }

    [Fact]
    public async Task EditFile_EmptyOldString_Throws()
    {
        // 空 old_string 是无意义操作：工具应明确报错而非静默写回原文件
        File.WriteAllText(Path.Combine(_dir, "e.txt"), "hello");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(new JsonObject { ["path"] = "e.txt", ["old_string"] = "", ["new_string"] = "x" }, ctx, CancellationToken.None));

        Assert.Contains("old_string", ex.Message);
    }

    [Fact]
    public async Task EditFile_IdenticalStrings_Throws()
    {
        // old_string == new_string 是无操作，应明确报错而非写入无意义撤销条目
        File.WriteAllText(Path.Combine(_dir, "id.txt"), "SAME");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(new JsonObject { ["path"] = "id.txt", ["old_string"] = "SAME", ["new_string"] = "SAME" }, ctx, CancellationToken.None));

        Assert.Contains("old_string", ex.Message);
    }

    [Fact]
    public async Task EditFile_SingleOccurrence_ReplacesAndReportsLine()
    {
        // 核心happy path：单次精确匹配应成功替换并报告修改起始行
        File.WriteAllText(Path.Combine(_dir, "single.txt"), "alpha\nbeta\ngamma\n");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "single.txt", ["old_string"] = "beta", ["new_string"] = "BETA" }, ctx, CancellationToken.None);

        Assert.Equal("alpha\nBETA\ngamma\n", File.ReadAllText(Path.Combine(_dir, "single.txt")));
        Assert.Contains("已替换 1 处", output);
        Assert.Contains("起始行 2", output);
    }

    [Fact]
    public async Task EditFile_LfOldString_OnCrlfFile_MatchesAndKeepsCrlf()
    {
        // 回归：模型输出几乎总是 LF，Windows 工程常是 CRLF——逐字匹配必失败。
        // 归一化重试命中后，替换片段与整个文件保持 CRLF 风格（不混入孤立 LF）
        File.WriteAllText(Path.Combine(_dir, "crlf.txt"), "line1\r\nline2\r\nline3\r\n");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var result = await tool.ExecuteAsync(
            new JsonObject
            {
                ["path"] = "crlf.txt",
                ["old_string"] = "line2\nline3",   // LF 版本
                ["new_string"] = "X\nY",           // LF 版本
            },
            ctx, CancellationToken.None);

        Assert.Contains("已替换 1 处", result);
        Assert.Contains("保留原 CRLF 换行", result); // 提示换行风格被保留
        var after = File.ReadAllText(Path.Combine(_dir, "crlf.txt"));
        Assert.Equal("line1\r\nX\r\nY\r\n", after);   // 替换成功且全文件保持 CRLF
    }

    [Fact]
    public async Task EditFile_LfFile_NoCrlfNote()
    {
        // LF 文件不应出现「保留原 CRLF 换行」提示（用原始 LF 字节写，避免 Windows 上 WriteAllText 写入 CRLF）
        File.WriteAllBytes(Path.Combine(_dir, "lf.txt"), System.Text.Encoding.UTF8.GetBytes("line1\nline2\n"));
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var result = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "lf.txt", ["old_string"] = "line2", ["new_string"] = "X" },
            ctx, CancellationToken.None);

        Assert.Contains("已替换 1 处", result);
        Assert.DoesNotContain("CRLF", result);
    }

    [Fact]
    public async Task EditFile_CrlfOldString_OnLfFile_MatchesAndKeepsLf()
    {
        // 反向：CRLF 的 old_string 编辑 LF 文件——命中后文件保持纯 LF
        File.WriteAllText(Path.Combine(_dir, "lf.txt"), "aa\nbb\ncc\n");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject
            {
                ["path"] = "lf.txt",
                ["old_string"] = "bb\r\ncc",   // CRLF 版本
                ["new_string"] = "Y\r\nZ",
            },
            ctx, CancellationToken.None);

        Assert.Equal("aa\nY\nZ\n", File.ReadAllText(Path.Combine(_dir, "lf.txt")));
    }

    [Fact]
    public async Task EditFile_LfOldString_ReplaceAll_CrlfFile()
    {
        // replace_all + 归一化：全部命中且保持 CRLF
        File.WriteAllText(Path.Combine(_dir, "crlf2.txt"), "x\r\nA\r\nx\r\nA\r\n");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var result = await tool.ExecuteAsync(
            new JsonObject
            {
                ["path"] = "crlf2.txt",
                ["old_string"] = "x\nA",
                ["new_string"] = "B\nC",
                ["replace_all"] = true,
            },
            ctx, CancellationToken.None);

        Assert.Contains("已替换 2 处", result);
        Assert.Equal("B\r\nC\r\nB\r\nC\r\n", File.ReadAllText(Path.Combine(_dir, "crlf2.txt")));
    }

    [Fact]
    public async Task EditFile_MissingPath_Throws()
    {
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(
                new JsonObject { ["old_string"] = "a", ["new_string"] = "b" },
                ctx, CancellationToken.None));
        Assert.Contains("path", ex.Message);
    }

    [Fact]
    public async Task EditFile_MissingOldStringParam_Throws()
    {
        File.WriteAllText(Path.Combine(_dir, "e.txt"), "abc");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(
                new JsonObject { ["path"] = "e.txt", ["new_string"] = "b" },
                ctx, CancellationToken.None));
        Assert.Contains("old_string", ex.Message);
    }

    [Fact]
    public async Task ListDirectory_DepthZero_OnlyRoot()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "top.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "sub", "deep.txt"), "y");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["depth"] = 0 }, ctx, CancellationToken.None);
        Assert.Contains("sub/", output);
        Assert.DoesNotContain("deep.txt", output); // 深度 0 不进入子目录
    }

    [Fact]
    public async Task ListDirectory_FileTarget_Throws()
    {
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(new JsonObject { ["path"] = "f.txt" }, ctx, CancellationToken.None));
        Assert.Contains("是文件", ex.Message);
    }

    [Fact]
    public async Task ListDirectory_MissingDir_Throws()
    {
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(new JsonObject { ["path"] = "no-such" }, ctx, CancellationToken.None));
        Assert.Contains("目录不存在", ex.Message);
    }

    [Fact]
    public async Task ReadFile_MissingFile_Throws()
    {
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(new JsonObject { ["path"] = "nope.txt" }, ctx, CancellationToken.None));
        Assert.Contains("文件不存在", ex.Message);
    }

    [Fact]
    public async Task ReadFile_DirectoryTarget_Throws()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "adir"));
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(new JsonObject { ["path"] = "adir" }, ctx, CancellationToken.None));
        Assert.Contains("是目录", ex.Message);
    }

    [Fact]
    public async Task WriteFile_IdenticalContent_SkipsWriteAndUndo()
    {
        // 回归：内容未变时曾照样重写文件并塞撤销条目；现在直接跳过
        File.WriteAllText(Path.Combine(_dir, "same.txt"), "stable");
        var before = File.GetLastWriteTimeUtc(Path.Combine(_dir, "same.txt"));
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        var result = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "same.txt", ["content"] = "stable" }, ctx, CancellationToken.None);

        Assert.Contains("跳过写入", result);
        Assert.Equal(0, ctx.Undo.Count); // 无撤销条目
        Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(_dir, "same.txt"))); // mtime 未变
    }

    [Fact]
    public async Task WriteFile_Append_AddsContentToExistingFile()
    {
        // append=true:在已有文件末尾追加内容（中间自动补换行）
        File.WriteAllText(Path.Combine(_dir, "app.txt"), "line1\n");
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        var result = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "app.txt", ["content"] = "line2", ["append"] = true }, ctx, CancellationToken.None);

        Assert.Equal("line1\nline2", File.ReadAllText(Path.Combine(_dir, "app.txt")));
        Assert.Contains("追加", result);
        Assert.Contains("2 行", result);
    }

    [Fact]
    public async Task WriteFile_Append_ToNewFile_CreatesFile()
    {
        // append=true 但文件不存在： behave 像普通写入
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "new_app.txt", ["content"] = "first", ["append"] = true }, ctx, CancellationToken.None);

        Assert.Equal("first", File.ReadAllText(Path.Combine(_dir, "new_app.txt")));
    }

    [Fact]
    public async Task WriteFile_Append_IdenticalContent_SkipsWrite()
    {
        // append=true 但追加后内容未变：跳过写入
        File.WriteAllText(Path.Combine(_dir, "same_app.txt"), "stable\n");
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        var result = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "same_app.txt", ["content"] = "", ["append"] = true }, ctx, CancellationToken.None);

        Assert.Contains("跳过写入", result);
        Assert.Equal(0, ctx.Undo.Count);
    }

    [Fact]
    public async Task WriteFile_Append_WithCreateDirs_CreatesParentsAndAppends()
    {
        // append=true + create_dirs=true:父目录不存在时自动创建并追加
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "sub/app.txt", ["content"] = "line1\n", ["append"] = true }, ctx, CancellationToken.None);

        Assert.Equal("line1\n", File.ReadAllText(Path.Combine(_dir, "sub", "app.txt")));
    }

    [Fact]
    public async Task WriteFile_Append_PreservesUndoStack()
    {
        // append=true:撤销应恢复原文件内容（不是追加后的内容）
        File.WriteAllText(Path.Combine(_dir, "undo_app.txt"), "original\n");
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "undo_app.txt", ["content"] = "appended\n", ["append"] = true }, ctx, CancellationToken.None);

        Assert.Equal(1, ctx.Undo.Count); // 有撤销条目
        ctx.Undo.TryUndo();
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(_dir, "undo_app.txt")));
    }

    [Fact]
    public async Task WriteFile_Append_WithCrlf_PreservesLineEndings()
    {
        // append=true:原文件 CRLF 和新内容 LF 都应保留（WriteTextPreserveEncoding 处理）
        File.WriteAllText(Path.Combine(_dir, "crlf_app.txt"), "line1\r\n");
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "crlf_app.txt", ["content"] = "line2", ["append"] = true }, ctx, CancellationToken.None);

        var text = File.ReadAllText(Path.Combine(_dir, "crlf_app.txt"));
        Assert.Contains("line1", text);
        Assert.Contains("line2", text);
    }

    [Fact]
    public async Task WriteFile_Append_EmptyFile_BehavesLikeWrite()
    {
        // append=true 追加到空文件： behave 像普通写入
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "empty_app.txt", ["content"] = "first", ["append"] = true }, ctx, CancellationToken.None);

        Assert.Equal("first", File.ReadAllText(Path.Combine(_dir, "empty_app.txt")));
    }

    [Fact]
    public async Task WriteFile_Append_WithIdenticalContent_SkipsWrite()
    {
        // append=true 但追加后内容未变化：跳过写入（不刷 mtime）
        File.WriteAllText(Path.Combine(_dir, "same_app.txt"), "stable\n");
        var before = File.GetLastWriteTimeUtc(Path.Combine(_dir, "same_app.txt"));
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        var result = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "same_app.txt", ["content"] = "", ["append"] = true }, ctx, CancellationToken.None);

        Assert.Contains("跳过写入", result);
        Assert.Equal(0, ctx.Undo.Count);
        Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(_dir, "same_app.txt")));
    }

    [Fact]
    public async Task WriteFile_Append_WithCreateDirsFalse_ExistingDir_Appends()
    {
        // append=true + create_dirs=false:父目录已存在时正常追加
        Directory.CreateDirectory(Path.Combine(_dir, "existing"));
        File.WriteAllText(Path.Combine(_dir, "existing", "cf.txt"), "old\n");
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "existing/cf.txt", ["content"] = "new\n", ["append"] = true, ["create_dirs"] = false }, ctx, CancellationToken.None);

        Assert.Equal("old\nnew\n", File.ReadAllText(Path.Combine(_dir, "existing", "cf.txt")));
    }

    [Fact]
    public async Task WriteFile_Append_WithCreateDirsFalse_MissingDir_Throws()
    {
        // append=true + create_dirs=false:父目录不存在时抛出清晰错误
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() => tool.ExecuteAsync(
            new JsonObject { ["path"] = "missing/app.txt", ["content"] = "new\n", ["append"] = true, ["create_dirs"] = false }, ctx, CancellationToken.None));

        Assert.Contains("父目录不存在", ex.Message);
    }

    [Fact]
    public async Task WriteFile_Append_LargeContent_AppendsSuccessfully()
    {
        // append=true:大内容追加成功，返回正确的字节数
        var largeContent = new string('X', 100_000);
        File.WriteAllText(Path.Combine(_dir, "large.txt"), "start\n");
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        var result = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "large.txt", ["content"] = largeContent, ["append"] = true }, ctx, CancellationToken.None);

        Assert.Contains("100,006", result); // 大内容被正确追加（含原有内容）
        var text = File.ReadAllText(Path.Combine(_dir, "large.txt"));
        Assert.StartsWith("start\n", text);
        Assert.EndsWith(new string('X', 100_000), text);
    }

    [Fact]
    public async Task WriteFile_Append_MultipleTimes_Accumulates()
    {
        // append=true 多次调用：内容依次累积
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(new JsonObject { ["path"] = "multi.txt", ["content"] = "a\n", ["append"] = true }, ctx, CancellationToken.None);
        await tool.ExecuteAsync(new JsonObject { ["path"] = "multi.txt", ["content"] = "b\n", ["append"] = true }, ctx, CancellationToken.None);
        await tool.ExecuteAsync(new JsonObject { ["path"] = "multi.txt", ["content"] = "c", ["append"] = true }, ctx, CancellationToken.None);

        Assert.Equal("a\nb\nc", File.ReadAllText(Path.Combine(_dir, "multi.txt")));
    }

    [Fact]
    public async Task WriteFile_Bom_NewFile_WritesUtf8Bom()
    {
        // bom=true + 新文件：写入 UTF-8 BOM
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "bom.txt", ["content"] = "hello", ["bom"] = true }, ctx, CancellationToken.None);

        var bytes = File.ReadAllBytes(Path.Combine(_dir, "bom.txt"));
        Assert.Equal(3, bytes.Take(3).Count(b => b == 0xEF || b == 0xBB || b == 0xBF)); // BOM 头
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
    }

    [Fact]
    public async Task WriteFile_Bom_ExistingFile_PreservesOriginalEncoding()
    {
        // bom=true + 已有文件：保留原编码（不强行加 BOM）
        File.WriteAllText(Path.Combine(_dir, "exist.txt"), "hello", new System.Text.UTF8Encoding(false));
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "exist.txt", ["content"] = "world", ["bom"] = true }, ctx, CancellationToken.None);

        var bytes = File.ReadAllBytes(Path.Combine(_dir, "exist.txt"));
        Assert.DoesNotContain(bytes, b => b == 0xEF && bytes.Length > 1 && bytes[1] == 0xBB); // 无 BOM
    }

    [Fact]
    public async Task WriteFile_LineEnding_Lf_ForcesLf()
    {
        // line_ending=lf:强制 LF 换行
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "lf.txt", ["content"] = "a\r\nb\r\nc", ["line_ending"] = "lf" }, ctx, CancellationToken.None);

        Assert.Equal("a\nb\nc", File.ReadAllText(Path.Combine(_dir, "lf.txt")));
    }

    [Fact]
    public async Task WriteFile_LineEnding_Crlf_ForcesCrlf()
    {
        // line_ending=crlf:强制 CRLF 换行
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "crlf.txt", ["content"] = "a\nb\nc", ["line_ending"] = "crlf" }, ctx, CancellationToken.None);

        Assert.Equal("a\r\nb\r\nc", File.ReadAllText(Path.Combine(_dir, "crlf.txt")));
    }

    [Fact]
    public async Task WriteFile_LineEnding_Preserve_MatchesExisting()
    {
        // line_ending=preserve:匹配已有文件的换行风格
        File.WriteAllText(Path.Combine(_dir, "exist.txt"), "a\r\nb\r\n");
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "exist.txt", ["content"] = "x\ny\nz", ["line_ending"] = "preserve" }, ctx, CancellationToken.None);

        Assert.Equal("x\r\ny\r\nz", File.ReadAllText(Path.Combine(_dir, "exist.txt"))); // 无尾部换行则不加
    }

    [Fact]
    public async Task WriteFile_LineEnding_Preserve_NewFile_UsesLf()
    {
        // line_ending=preserve + 新文件：默认 LF
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "new.txt", ["content"] = "a\nb\n", ["line_ending"] = "preserve" }, ctx, CancellationToken.None);

        Assert.Equal("a\nb\n", File.ReadAllText(Path.Combine(_dir, "new.txt")));
    }

    [Fact]
    public async Task WriteFile_Backup_CreatesBakBeforeOverwrite()
    {
        // backup=true:覆盖已有文件前创建 .bak 副本
        File.WriteAllText(Path.Combine(_dir, "orig.txt"), "old content");
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "orig.txt", ["content"] = "new content", ["backup"] = true }, ctx, CancellationToken.None);

        Assert.Equal("new content", File.ReadAllText(Path.Combine(_dir, "orig.txt")));
        Assert.Equal("old content", File.ReadAllText(Path.Combine(_dir, "orig.txt.bak"))); // .bak 保留原内容
    }

    [Fact]
    public async Task WriteFile_Backup_NewFile_NoBakCreated()
    {
        // backup=true + 新文件：不创建 .bak（因为无旧文件可备份）
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "fresh.txt", ["content"] = "hello", ["backup"] = true }, ctx, CancellationToken.None);

        Assert.Equal("hello", File.ReadAllText(Path.Combine(_dir, "fresh.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "fresh.txt.bak"))); // 无 .bak
    }

    [Fact]
    public async Task WriteFile_PreserveTrailingNewline_True_KeepsNewline()
    {
        // preserve_trailing_newline=true（默认）:保留 content 末尾换行
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "keep.txt", ["content"] = "hello\n", ["preserve_trailing_newline"] = true }, ctx, CancellationToken.None);

        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(_dir, "keep.txt")));
    }

    [Fact]
    public async Task WriteFile_PreserveTrailingNewline_False_StripsNewline()
    {
        // preserve_trailing_newline=false:去掉 content 末尾换行
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "strip.txt", ["content"] = "hello\n", ["preserve_trailing_newline"] = false }, ctx, CancellationToken.None);

        Assert.Equal("hello", File.ReadAllText(Path.Combine(_dir, "strip.txt"))); // 末尾换行被去掉
    }

    [Fact]
    public async Task EditFile_DryRun_DoesNotModifyFileOrUndoStack()
    {
        // dry_run=true:预览改动但不写盘，不污染撤销栈
        File.WriteAllText(Path.Combine(_dir, "dry.txt"), "hello world");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var result = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "dry.txt", ["old_string"] = "world", ["new_string"] = "moon", ["dry_run"] = true },
            ctx, CancellationToken.None);

        Assert.Contains("[dry_run]", result);
        Assert.Contains("将替换", result);
        Assert.Equal("hello world", File.ReadAllText(Path.Combine(_dir, "dry.txt"))); // 文件未变
        Assert.Equal(0, ctx.Undo.Count); // 撤销栈未污染
    }

    [Fact]
    public async Task EditFile_IdenticalStrings_ThrowsInsteadOfNoOp()
    {
        // 回归：old == new 曾静默重写文件并报「已替换 N 处」，误导模型以为做了修改
        File.WriteAllText(Path.Combine(_dir, "eq.txt"), "value");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(
                new JsonObject { ["path"] = "eq.txt", ["old_string"] = "value", ["new_string"] = "value" },
                ctx, CancellationToken.None));
        Assert.Contains("相同", ex.Message);
        Assert.Equal(0, ctx.Undo.Count);
    }

    [Fact]
    public async Task EditFile_ReplaceEntireFile_WithEmptyString()
    {
        // 全量替换：old_string == 整个文件 → new_string 为空时应得到空文件（可撤销）
        File.WriteAllText(Path.Combine(_dir, "full.txt"), "only content here\n");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var result = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "full.txt", ["old_string"] = "only content here\n", ["new_string"] = "" },
            ctx, CancellationToken.None);

        Assert.Contains("已替换 1 处", result);
        Assert.Equal("", File.ReadAllText(Path.Combine(_dir, "full.txt")));
        Assert.Equal(1, ctx.Undo.Count); // 可撤销
    }

    [Fact]
    public async Task ReadFile_GbkEncoded_DecodesViaFallback()
    {
        // 无 BOM 的 GBK 文件：UTF-8 严格校验失败 → GB18030 兜底，内容可读而非替换符
        _ = TextUtil.EstimateTokens("");
        var gbk = System.Text.Encoding.GetEncoding("GB18030");
        File.WriteAllBytes(Path.Combine(_dir, "ansi.txt"), gbk.GetBytes("中文内容"));
        var tool = new ReadFileTool();
        var ctx = new AgentContext
        {
            Config = new AgentConfig(),
            Workspace = new Workspace(_dir),
        };

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "ansi.txt" }, ctx, CancellationToken.None);

        Assert.Contains("中文内容", output);
    }

    [Fact]
    public async Task ReadFile_MaxLineLength_ExactlyAtCap_NoTruncation()
    {
        // max_line_length 恰好等于 50000 时不截断
        var path = Path.Combine(_dir, "cap.txt");
        File.WriteAllText(path, new string('A', 50000) + "\nend\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "cap.txt", ["max_line_length"] = 50000 }, ctx, CancellationToken.None);

        Assert.DoesNotContain("…", output);
        Assert.Contains("end", output);
    }

    [Fact]
    public async Task ReadFile_MaxLineLength_OverMax_ClampsTo50000()
    {
        // max_line_length 上限 50000：超限输入会被 clamp，不抛出异常
        var path = Path.Combine(_dir, "big.txt");
        File.WriteAllText(path, new string('Z', 100_000) + "\nend\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "big.txt", ["max_line_length"] = 100_000 }, ctx, CancellationToken.None);

        Assert.Contains("…", output); // 被 50000 截断
        Assert.Contains("end", output);
    }

    [Fact]
    public async Task ReadFile_HeadAtMax_ReturnsUpTo5000Lines()
    {
        // head 上限 5000：写 5001 行，head=5000 时应精确返回前 5000 行
        var path = Path.Combine(_dir, "many.txt");
        File.WriteAllText(path, string.Join("\n", Enumerable.Range(0, 5001).Select(i => $"L{i:D5}")) + "\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "many.txt", ["head"] = 5000 }, ctx, CancellationToken.None);

        Assert.Contains("L00000", output);
        Assert.DoesNotContain("L05000", output); // 第 5001 行被 head 截掉
    }

    [Fact]
    public async Task ReadFile_SkipEmpty_AllEmptyFile_ReturnsEmptyOutput()
    {
        // 全空文件 + skip_empty:结果为空字符串（而非"(文件为空)"）
        File.WriteAllText(Path.Combine(_dir, "empty.txt"), "\n\n\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "empty.txt", ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Equal("（empty.txt 共 3 行）\n", output); // 空行全被 skip，仅剩头部 + 尾换行
    }

    [Fact]
    public async Task ReadFile_SkipEmpty_Offset_StillSkipsBlanksWithinRange()
    {
        // skip_empty + offset:只在 offset 开始的范围内跳过空行
        File.WriteAllText(Path.Combine(_dir, "so.txt"), "a\n\nb\n\nc\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "so.txt", ["skip_empty"] = true, ["offset"] = 2 }, ctx, CancellationToken.None);

        Assert.Contains("3\tb", output); // 第 2 行空行被跳过,b 在第 3 行
        Assert.DoesNotContain("1\t", output); // offset 之前的内容不出现在输出
    }

    [Fact]
    public async Task ReadFile_SkipEmpty_Head_StillSkipsBlanksWithinRange()
    {
        // skip_empty + head:只在 head 范围内跳过空行
        File.WriteAllText(Path.Combine(_dir, "sh.txt"), "a\n\nb\nc\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "sh.txt", ["skip_empty"] = true, ["head"] = 3 }, ctx, CancellationToken.None);

        Assert.Contains("1\ta", output);
        Assert.Contains("3\tb", output); // 第 2 行空行被跳过
        Assert.DoesNotContain("4\tc", output); // head=3 范围外
    }

    [Fact]
    public async Task ReadFile_SkipEmpty_Tail_StillSkipsBlanksWithinRange()
    {
        // skip_empty + tail:只在 tail 范围内跳过空行
        File.WriteAllText(Path.Combine(_dir, "st.txt"), "a\n\nb\n\nc\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "st.txt", ["skip_empty"] = true, ["tail"] = 2 }, ctx, CancellationToken.None);

        Assert.Contains("5\tc", output); // tail 范围内最后一个非空行
        Assert.DoesNotContain("4\t", output); // 空行被跳过
    }

    [Fact]
    public async Task ReadFile_SkipEmpty_WithNoEncodingNote_HidesBlanksAndSuppressesHint()
    {
        // skip_empty + no_encoding_note:空行隐藏,编码提示也隐藏
        File.WriteAllText(Path.Combine(_dir, "combo2.txt"), "a\n\nb\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "combo2.txt", ["skip_empty"] = true, ["no_encoding_note"] = true }, ctx, CancellationToken.None);

        Assert.Contains("1\ta", output);
        Assert.Contains("3\tb", output);
        Assert.DoesNotContain("2\t", output); // 空行被跳过
        Assert.DoesNotContain("（编码", output);
    }

    [Fact]
    public async Task ReadFile_SkipEmpty_AlsoSkipsWhitespaceOnlyLines()
    {
        // skip_empty:空白行(空格、Tab)也视为空行跳过
        File.WriteAllText(Path.Combine(_dir, "ws.txt"), "a\n   \n\t\nb\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "ws.txt", ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("1\ta", output);
        Assert.Contains("4\tb", output);
        Assert.DoesNotContain("2\t", output);
        Assert.DoesNotContain("3\t", output);
    }

    [Fact]
    public async Task ReadFile_Trim_StripsLeadingAndTrailingWhitespace()
    {
        // trim=true:每行首尾空白被去掉，行号仍保留
        File.WriteAllText(Path.Combine(_dir, "trim.txt"), "  a  \n\tb\t\n  \nc  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "trim.txt", ["trim"] = true }, ctx, CancellationToken.None);

        Assert.Contains("1\ta", output);
        Assert.Contains("2\tb", output);
        Assert.Contains("4\tc", output); // c 在第 4 行
        Assert.DoesNotContain("  a", output);
        Assert.DoesNotContain("\t\t", output);
    }

    [Fact]
    public async Task ReadFile_Trim_WithSkipEmpty_HidesBlankLines()
    {
        // trim + skip_empty:空白行被跳过，非空行的首尾空白被trim
        File.WriteAllText(Path.Combine(_dir, "tse.txt"), "  a  \n   \n\tb\t\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tse.txt", ["trim"] = true, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("1\ta", output);
        Assert.Contains("3\tb", output);
        Assert.DoesNotContain("2\t", output); // 空白行被跳过
    }

    [Fact]
    public async Task ReadFile_Trim_WithNoLineNumbers_OutputsCleanText()
    {
        // trim + no_line_numbers:无行号，首尾空白去掉
        File.WriteAllText(Path.Combine(_dir, "tnl.txt"), "  hello  \n\tworld\t\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tnl.txt", ["trim"] = true, ["no_line_numbers"] = true }, ctx, CancellationToken.None);

        Assert.Contains("hello" + Environment.NewLine + "world", output);
        Assert.DoesNotContain("  hello", output);
        Assert.DoesNotContain("\t\t", output);
    }

    [Fact]
    public async Task ReadFile_Trim_WithMaxLineLength_TrimsThenTruncates()
    {
        // trim + max_line_length:先 trim 再截断（trim 不影响截断计数）
        File.WriteAllText(Path.Combine(_dir, "tml.txt"), "  " + new string('X', 100) + "  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tml.txt", ["trim"] = true, ["max_line_length"] = 50 }, ctx, CancellationToken.None);

        Assert.Contains("…", output); // 被截断
        Assert.DoesNotContain("  ", output); // 首尾空白被 trim
    }

    [Fact]
    public async Task ReadFile_Trim_WithHead_TrimsWithinHeadRange()
    {
        // trim + head:只在 head 范围内 trim
        File.WriteAllText(Path.Combine(_dir, "th.txt"), "  a  \n  b  \nc\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "th.txt", ["trim"] = true, ["head"] = 2 }, ctx, CancellationToken.None);

        Assert.Contains("1\ta", output);
        Assert.Contains("2\tb", output);
        Assert.DoesNotContain("3\tc", output); // head=2 范围外
    }

    [Fact]
    public async Task ReadFile_Trim_WithTail_TrimsWithinTailRange()
    {
        // trim + tail:只在 tail 范围内 trim
        File.WriteAllText(Path.Combine(_dir, "tt.txt"), "  a  \n  b  \nc\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tt.txt", ["trim"] = true, ["tail"] = 2 }, ctx, CancellationToken.None);

        Assert.Contains("2\tb", output);
        Assert.Contains("3\tc", output);
        Assert.DoesNotContain("1\ta", output); // tail 范围外
    }

    [Fact]
    public async Task ReadFile_Trim_WithOffset_TrimsWithinOffsetRange()
    {
        // trim + offset:只在 offset 范围内 trim
        File.WriteAllText(Path.Combine(_dir, "to.txt"), "  a  \n  b  \n  c  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "to.txt", ["trim"] = true, ["offset"] = 2 }, ctx, CancellationToken.None);

        Assert.Contains("2\tb", output);
        Assert.Contains("3\tc", output);
        Assert.DoesNotContain("1\ta", output); // offset 范围外
    }

    [Fact]
    public async Task ReadFile_Trim_WithRaw_TrimIsRedundantButAllowed()
    {
        // raw 已隐含不截断/无行号；trim=true 在 raw 下仍生效（去掉首尾空白）
        File.WriteAllText(Path.Combine(_dir, "tr.txt"), "  hello  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tr.txt", ["trim"] = true, ["raw"] = true }, ctx, CancellationToken.None);

        Assert.Equal("hello", output); // 无行号，无空白，无尾换行
    }

    [Fact]
    public async Task ReadFile_Trim_WithNoEncodingNote_TrimsAndSuppressesHint()
    {
        // trim + no_encoding_note:空白被 trim，编码提示也隐藏
        File.WriteAllText(Path.Combine(_dir, "ten.txt"), "  a  \n  b  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "ten.txt", ["trim"] = true, ["no_encoding_note"] = true }, ctx, CancellationToken.None);

        Assert.Contains("1\ta", output);
        Assert.Contains("2\tb", output);
        Assert.DoesNotContain("（编码", output);
    }

    [Fact]
    public async Task ReadFile_Trim_AllWhitespaceLine_BecomesEmpty()
    {
        // trim=true:全空白行 trim 后变成空串，但仍占一行（行号保留）
        File.WriteAllText(Path.Combine(_dir, "tw.txt"), "  \n\ta\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tw.txt", ["trim"] = true }, ctx, CancellationToken.None);

        Assert.Contains("1\t", output); // 第 1 行 trim 后为空
        Assert.Contains("2\ta", output);
    }

    [Fact]
    public async Task ReadFile_Trim_WithSkipEmpty_TrimsThenSkipsEmptyLines()
    {
        // trim + skip_empty:先 trim 再跳过空白行（trim 后的空行也会被跳过）
        File.WriteAllText(Path.Combine(_dir, "tse2.txt"), "  \n\ta  \n   \nb\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tse2.txt", ["trim"] = true, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("2\ta", output); // trim 后非空
        Assert.Contains("4\tb", output);
        Assert.DoesNotContain("1\t", output); // trim 后空行被 skip_empty 跳过
        Assert.DoesNotContain("3\t", output);
    }

    [Fact]
    public async Task ReadFile_Trim_MixedLineEndings_TrimsEachLine()
    {
        // trim=true:混合换行风格的文件，每行独立 trim
        File.WriteAllText(Path.Combine(_dir, "mix.txt"), "  a  \r\n\tb\t\n  c  \r\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "mix.txt", ["trim"] = true }, ctx, CancellationToken.None);

        Assert.Contains("1\ta", output);
        Assert.Contains("2\tb", output);
        Assert.Contains("3\tc", output);
    }

    [Fact]
    public async Task ReadFile_Trim_AllWhitespaceFile_ProducesEmptyLines()
    {
        // trim=true:全空白文件 trim 后每行仍占位（空内容 + 行号），只有 skip_empty 才会跳过
        File.WriteAllText(Path.Combine(_dir, "aw.txt"), "  \n\t\n   \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "aw.txt", ["trim"] = true }, ctx, CancellationToken.None);

        Assert.Contains("1\t", output);
        Assert.Contains("2\t", output);
        Assert.Contains("3", output); // 第 3 行 trim 后为空，行号仍保留
        Assert.DoesNotContain("  ", output); // 首尾空白被 trim
    }

    [Fact]
    public async Task ReadFile_Trim_WithRaw_AllWhitespace_ReturnsTrimmedContent()
    {
        // trim + raw:全空白行 trim 后为空，raw 模式仍输出（无行号）
        File.WriteAllText(Path.Combine(_dir, "twr.txt"), "  \n\ta  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "twr.txt", ["trim"] = true, ["raw"] = true }, ctx, CancellationToken.None);

        Assert.Contains("a", output); // trim 后只剩非空行
        Assert.DoesNotContain("  ", output); // 首尾空白被 trim
    }

    [Fact]
    public async Task ReadFile_Trim_WithSkipEmpty_TrimsThenSkipsBlankLines()
    {
        // trim + skip_empty:trim 后的空行也会被 skip_empty 跳过
        File.WriteAllText(Path.Combine(_dir, "tse3.txt"), "  \n\ta  \n   \nb\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tse3.txt", ["trim"] = true, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("2\ta", output);
        Assert.Contains("4\tb", output);
        Assert.DoesNotContain("1\t", output); // trim 后空行被 skip
        Assert.DoesNotContain("3\t", output);
    }

    [Fact]
    public async Task ReadFile_Trim_LeadingTrailingBlanks_AreTrimmed()
    {
        // trim=true:首尾空白行被 trim（变成空串），但行号仍保留
        File.WriteAllText(Path.Combine(_dir, "lt.txt"), "  \n  middle  \n   \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "lt.txt", ["trim"] = true }, ctx, CancellationToken.None);

        Assert.Contains("1\t", output); // trim 后空行仍占位
        Assert.Contains("2\tmiddle", output);
        Assert.Contains("3", output); // 第 3 行 trim 后为空
    }

    [Fact]
    public async Task ReadFile_Trim_WithMaxLineLength_TrimsBeforeTruncate()
    {
        // trim + max_line_length:先 trim 再截断（trim 移除的首尾空白不计入截断长度）
        File.WriteAllText(Path.Combine(_dir, "tml2.txt"), "  " + new string('X', 100) + "  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tml2.txt", ["trim"] = true, ["max_line_length"] = 50 }, ctx, CancellationToken.None);

        Assert.Contains("…", output); // 截断生效
        Assert.DoesNotContain("  ", output); // 首尾空白被 trim
    }

    [Fact]
    public async Task ReadFile_Trim_WithNoLineNumbersAndMaxLineLength_OutputsCleanText()
    {
        // trim + no_line_numbers + max_line_length:无行号，trim 后截断
        File.WriteAllText(Path.Combine(_dir, "tnlml.txt"), "  hello world  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tnlml.txt", ["trim"] = true, ["no_line_numbers"] = true, ["max_line_length"] = 5 }, ctx, CancellationToken.None);

        Assert.Contains("（tnlml.txt 共 1 行）", output); // 头部仍在（no_line_numbers 不删头部）
        Assert.Contains("hel", output); // 内容被截断（max_line_length=5 含省略号）
    }

    [Fact]
    public async Task ReadFile_Trim_WithHeadAndMaxLineLength_TrimsThenTruncatesInRange()
    {
        // trim + head + max_line_length:先在 head 范围内 trim，再截断
        File.WriteAllText(Path.Combine(_dir, "thml.txt"), "  hello world  \n  foo bar  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "thml.txt", ["trim"] = true, ["head"] = 1, ["max_line_length"] = 8 }, ctx, CancellationToken.None);

        Assert.Contains("hello", output); // trim 后仍含原词
        Assert.Contains("…", output); // 截断生效
        Assert.DoesNotContain("foo", output); // head=1 范围外
    }

    [Fact]
    public async Task ReadFile_Trim_WithTailAndMaxLineLength_TrimsThenTruncatesInRange()
    {
        // trim + tail + max_line_length:先在 tail 范围内 trim，再截断
        File.WriteAllText(Path.Combine(_dir, "ttml.txt"), "  hello world  \n  foo bar  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "ttml.txt", ["trim"] = true, ["tail"] = 1, ["max_line_length"] = 8 }, ctx, CancellationToken.None);

        Assert.Contains("foo", output); // tail=1 显示最后一行
        Assert.Contains("…", output); // 截断生效
        Assert.DoesNotContain("hello", output); // tail 范围外
    }

    [Fact]
    public async Task ReadFile_Trim_WithOffsetAndMaxLineLength_TrimsThenTruncatesInRange()
    {
        // trim + offset + max_line_length:先在 offset 范围内 trim，再截断
        File.WriteAllText(Path.Combine(_dir, "toml.txt"), "  hello world  \n  foo bar  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "toml.txt", ["trim"] = true, ["offset"] = 2, ["max_line_length"] = 8 }, ctx, CancellationToken.None);

        Assert.Contains("foo", output); // offset=2 显示第 2 行
        Assert.Contains("…", output); // 截断生效
        Assert.DoesNotContain("hello", output); // offset 范围外
    }

    [Fact]
    public async Task ReadFile_Trim_WithHeadAndTail_TrimsInRange()
    {
        // trim + head + tail:先在 head/tail 范围内 trim
        File.WriteAllText(Path.Combine(_dir, "tht.txt"), "  a  \n  b  \n  c  \n  d  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tht.txt", ["trim"] = true, ["head"] = 3, ["tail"] = 2 }, ctx, CancellationToken.None);

        Assert.Contains("3\tc", output); // tail=2 显示最后 2 行（head 在 tail 存在时被忽略）
        Assert.Contains("4\td", output);
        Assert.DoesNotContain("1\ta", output);
        Assert.DoesNotContain("2\tb", output);
    }

    [Fact]
    public async Task ReadFile_Trim_WithSkipEmptyAndNoEncodingNote_HidesBlanksAndSuppressesHint()
    {
        // trim + skip_empty + no_encoding_note:空白被 trim 并跳过，编码提示也隐藏
        File.WriteAllText(Path.Combine(_dir, "tsenn.txt"), "  \n\ta  \n   \nb\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tsenn.txt", ["trim"] = true, ["skip_empty"] = true, ["no_encoding_note"] = true }, ctx, CancellationToken.None);

        Assert.Contains("2\ta", output);
        Assert.Contains("4\tb", output);
        Assert.DoesNotContain("1\t", output); // trim 后空行被 skip
        Assert.DoesNotContain("3\t", output);
        Assert.DoesNotContain("（编码", output); // 编码提示隐藏
    }

    [Fact]
    public async Task ReadFile_Trim_WithRawAndNoEncodingNote_TrimsAndSuppressesHint()
    {
        // trim + raw + no_encoding_note:raw 已隐含无行号/无编码提示，trim 仍生效
        File.WriteAllText(Path.Combine(_dir, "trn.txt"), "  hello  \n  world  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "trn.txt", ["trim"] = true, ["raw"] = true, ["no_encoding_note"] = true }, ctx, CancellationToken.None);

        Assert.Contains("hello", output);
        Assert.Contains("world", output);
        Assert.DoesNotContain("  ", output); // 首尾空白被 trim
    }

    [Fact]
    public async Task ReadFile_Trim_WithRawAndSkipEmpty_TrimsAndSkipsBlanks()
    {
        // trim + raw + skip_empty:空白被 trim 并跳过，raw 模式无行号
        File.WriteAllText(Path.Combine(_dir, "trs.txt"), "  \n\ta  \n   \nb\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "trs.txt", ["trim"] = true, ["raw"] = true, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("a" + Environment.NewLine + "b", output); // 只有非空行
        Assert.DoesNotContain("  ", output); // 首尾空白被 trim
    }

    [Fact]
    public async Task ReadFile_Trim_WithHeadAndSkipEmpty_TrimsAndSkipsInHeadRange()
    {
        // trim + head + skip_empty:在 head 范围内 trim 并跳过空白行
        File.WriteAllText(Path.Combine(_dir, "ths.txt"), "  \n  a  \n   \nb\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "ths.txt", ["trim"] = true, ["head"] = 3, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("2\ta", output); // head=3 范围内，trim 后非空
        Assert.DoesNotContain("1\t", output); // trim 后空行被 skip
        Assert.DoesNotContain("b", output); // head 范围外
    }

    [Fact]
    public async Task ReadFile_Trim_WithTailAndSkipEmpty_TrimsAndSkipsInTailRange()
    {
        // trim + tail + skip_empty:在 tail 范围内 trim 并跳过空白行
        File.WriteAllText(Path.Combine(_dir, "tts.txt"), "  \n  a  \n   \nb\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tts.txt", ["trim"] = true, ["tail"] = 2, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("4\tb", output); // tail=2 显示最后 2 行，但空白行被 skip
        Assert.DoesNotContain("1\t", output);
        Assert.DoesNotContain("2\ta", output); // 被 skip_empty 跳过
        Assert.DoesNotContain("3\t", output); // 空白行被 skip
    }

    [Fact]
    public async Task ReadFile_Trim_WithOffsetAndSkipEmpty_TrimsAndSkipsInOffsetRange()
    {
        // trim + offset + skip_empty:在 offset 范围内 trim 并跳过空白行
        File.WriteAllText(Path.Combine(_dir, "tos.txt"), "  \n  a  \n   \nb\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tos.txt", ["trim"] = true, ["offset"] = 2, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("2\ta", output); // offset=2 显示第 2 行起
        Assert.DoesNotContain("1\t", output);
        Assert.DoesNotContain("3\t", output); // 被 skip_empty 跳过
    }

    [Fact]
    public async Task ReadFile_Trim_WithHeadAndTailAndSkipEmpty_TrimsAndSkipsInRange()
    {
        // trim + head + tail + skip_empty:在 head/tail 范围内 trim 并跳过空白行
        File.WriteAllText(Path.Combine(_dir, "thts.txt"), "  \n  a  \n   \n  b  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "thts.txt", ["trim"] = true, ["head"] = 3, ["tail"] = 2, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("4\tb", output); // tail=2 显示最后 2 行（head 被忽略）
        Assert.DoesNotContain("1\t", output);
        Assert.DoesNotContain("2\ta", output); // tail 范围外
        Assert.DoesNotContain("3\t", output); // 空白行被 skip
    }

    [Fact]
    public async Task ReadFile_Trim_WithHeadAndTailAndMaxLineLength_TrimsThenTruncatesInRange()
    {
        // trim + head + tail + max_line_length:在范围内 trim 并截断
        File.WriteAllText(Path.Combine(_dir, "thtml.txt"), "  hello world  \n  foo bar  \n  baz  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "thtml.txt", ["trim"] = true, ["head"] = 2, ["tail"] = 2, ["max_line_length"] = 8 }, ctx, CancellationToken.None);

        Assert.Contains("foo", output); // head=2, tail=2 显示后 2 行（head 被忽略）
        Assert.Contains("…", output); // 截断生效
    }

    [Fact]
    public async Task ReadFile_Trim_WithOffsetAndLimitAndMaxLineLength_TrimsThenTruncatesInRange()
    {
        // trim + offset + limit + max_line_length:在 offset/limit 范围内 trim 并截断
        File.WriteAllText(Path.Combine(_dir, "tolml.txt"), "  hello world  \n  foo bar  \n  baz  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tolml.txt", ["trim"] = true, ["offset"] = 2, ["limit"] = 1, ["max_line_length"] = 8 }, ctx, CancellationToken.None);

        Assert.Contains("foo", output); // offset=2, limit=1 显示第 2 行
        Assert.Contains("…", output); // 截断生效
        Assert.DoesNotContain("hello", output); // offset 范围外
    }

    [Fact]
    public async Task ReadFile_Trim_WithHeadAndSkipEmptyAndMaxLineLength_TrimsThenTruncatesInRange()
    {
        // trim + head + skip_empty + max_line_length:在 head 范围内 trim、skip、截断
        File.WriteAllText(Path.Combine(_dir, "thsml.txt"), "  \n  hello world  \n   \n  foo bar  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "thsml.txt", ["trim"] = true, ["head"] = 3, ["skip_empty"] = true, ["max_line_length"] = 8 }, ctx, CancellationToken.None);

        Assert.Contains("hello", output); // head=3 范围内 trim 后非空
        Assert.Contains("…", output); // 截断生效
        Assert.DoesNotContain("1\t", output); // 空白行被 skip
        Assert.DoesNotContain("foo", output); // head 范围外
    }

    [Fact]
    public async Task ReadFile_Trim_WithTailAndSkipEmptyAndMaxLineLength_TrimsThenTruncatesInRange()
    {
        // trim + tail + skip_empty + max_line_length:在 tail 范围内 trim、skip、截断
        File.WriteAllText(Path.Combine(_dir, "ttsml.txt"), "  \n  hello world  \n   \n  foo bar  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "ttsml.txt", ["trim"] = true, ["tail"] = 2, ["skip_empty"] = true, ["max_line_length"] = 8 }, ctx, CancellationToken.None);

        Assert.Contains("foo", output); // tail=2 范围内 trim 后非空
        Assert.Contains("…", output); // 截断生效
        Assert.DoesNotContain("hello", output); // tail 范围外
    }

    [Fact]
    public async Task ReadFile_Trim_WithHeadAndTailAndSkipEmptyAndMaxLineLength_TrimsThenTruncatesInRange()
    {
        // trim + head + tail + skip_empty + max_line_length:在范围内 trim、skip、截断
        File.WriteAllText(Path.Combine(_dir, "thtsml.txt"), "  \n  hello world  \n   \n  foo bar  \n  baz  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "thtsml.txt", ["trim"] = true, ["head"] = 3, ["tail"] = 2, ["skip_empty"] = true, ["max_line_length"] = 8 }, ctx, CancellationToken.None);

        Assert.Contains("foo", output); // tail=2 范围内 trim 后非空
        Assert.Contains("…", output); // 截断生效
        Assert.DoesNotContain("hello", output); // tail 范围外
    }

    [Fact]
    public async Task ReadFile_Trim_WithRawAndHeadAndSkipEmpty_TrimsAndSkipsInRawMode()
    {
        // trim + raw + head + skip_empty:raw 模式无行号，trim 并跳过空白行
        File.WriteAllText(Path.Combine(_dir, "trhs.txt"), "  \n  hello  \n   \nworld\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "trhs.txt", ["trim"] = true, ["raw"] = true, ["head"] = 3, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("hello", output); // head=3 范围内只有 hello 非空
        Assert.DoesNotContain("world", output); // head 范围外
        Assert.DoesNotContain("  ", output); // 首尾空白被 trim
    }

    [Fact]
    public async Task ReadFile_Trim_WithRawAndTailAndSkipEmpty_TrimsAndSkipsInRawMode()
    {
        // trim + raw + tail + skip_empty:raw 模式无行号，trim 并跳过空白行
        File.WriteAllText(Path.Combine(_dir, "trts.txt"), "  \n  hello  \n   \nworld\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "trts.txt", ["trim"] = true, ["raw"] = true, ["tail"] = 2, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("world", output); // tail=2 范围内只有 world 非空
        Assert.DoesNotContain("hello", output); // tail 范围外
        Assert.DoesNotContain("  ", output); // 首尾空白被 trim
    }

    [Fact]
    public async Task ReadFile_Trim_WithRawAndHeadAndTailAndSkipEmpty_TrimsAndSkipsInRawMode()
    {
        // trim + raw + head + tail + skip_empty:raw 模式无行号，trim 并跳过空白行
        File.WriteAllText(Path.Combine(_dir, "trhts.txt"), "  \n  hello  \n   \nworld\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "trhts.txt", ["trim"] = true, ["raw"] = true, ["head"] = 3, ["tail"] = 2, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("world", output); // tail=2 范围内只有 world 非空
        Assert.DoesNotContain("hello", output); // tail 范围外
        Assert.DoesNotContain("  ", output); // 首尾空白被 trim
    }

    [Fact]
    public async Task ReadFile_Trim_WithRawAndHeadAndSkipEmptyAndMaxLineLength_TrimsThenTruncatesInRawMode()
    {
        // trim + raw + head + skip_empty + max_line_length:raw 模式下 trim、skip、截断
        File.WriteAllText(Path.Combine(_dir, "trhsml.txt"), "  \n  hello world  \n   \nfoo\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "trhsml.txt", ["trim"] = true, ["raw"] = true, ["head"] = 3, ["skip_empty"] = true, ["max_line_length"] = 8 }, ctx, CancellationToken.None);

        Assert.Contains("hello world", output); // head=3 范围内 trim 后非空（raw 不截断）
        Assert.DoesNotContain("foo", output); // head 范围外
    }

    [Fact]
    public async Task ReadFile_Trim_WithRawAndTailAndSkipEmptyAndMaxLineLength_TrimsThenTruncatesInRawMode()
    {
        // trim + raw + tail + skip_empty + max_line_length:raw 模式下 trim、skip、截断
        File.WriteAllText(Path.Combine(_dir, "trtsml.txt"), "  \n  hello world  \n   \nfoo\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "trtsml.txt", ["trim"] = true, ["raw"] = true, ["tail"] = 2, ["skip_empty"] = true, ["max_line_length"] = 8 }, ctx, CancellationToken.None);

        Assert.Contains("foo", output); // tail=2 范围内 trim 后非空（raw 不截断）
        Assert.DoesNotContain("hello", output); // tail 范围外
    }

    [Fact]
    public async Task ReadFile_Trim_WithRawAndHeadAndTailAndSkipEmptyAndMaxLineLength_TrimsThenTruncatesInRawMode()
    {
        // trim + raw + head + tail + skip_empty + max_line_length:raw 模式下 trim、skip、截断
        File.WriteAllText(Path.Combine(_dir, "trhtsml.txt"), "  \n  hello world  \n   \nfoo\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "trhtsml.txt", ["trim"] = true, ["raw"] = true, ["head"] = 3, ["tail"] = 2, ["skip_empty"] = true, ["max_line_length"] = 8 }, ctx, CancellationToken.None);

        Assert.Contains("foo", output); // tail=2 范围内 trim 后非空（raw 不截断）
        Assert.DoesNotContain("hello", output); // tail 范围外
    }

    [Fact]
    public async Task ReadFile_Trim_WithRawAndOffsetAndSkipEmpty_TrimsAndSkipsInRawMode()
    {
        // trim + raw + offset + skip_empty:raw 模式下在 offset 范围内 trim 并跳过空白行
        File.WriteAllText(Path.Combine(_dir, "tros.txt"), "  \n  hello  \n   \nworld\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "tros.txt", ["trim"] = true, ["raw"] = true, ["offset"] = 2, ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("hello" + Environment.NewLine + "world", output); // 只有非空行
        Assert.DoesNotContain("  ", output); // 首尾空白被 trim
    }

    [Fact]
    public async Task ReadFile_Trim_WithRawAndOffsetAndSkipEmptyAndMaxLineLength_TrimsThenTruncatesInRawMode()
    {
        // trim + raw + offset + skip_empty + max_line_length:raw 不截断，但验证组合不报错
        File.WriteAllText(Path.Combine(_dir, "trosml.txt"), "  \n  hello world  \n   \nfoo\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "trosml.txt", ["trim"] = true, ["raw"] = true, ["offset"] = 2, ["skip_empty"] = true, ["max_line_length"] = 8 }, ctx, CancellationToken.None);

        Assert.Contains("hello world", output); // raw 不截断
        Assert.DoesNotContain("  ", output); // 首尾空白被 trim
    }

    [Fact]
    public async Task ReadFile_Trim_WithRawAndOffsetAndLimit_TrimsInRawMode()
    {
        // trim + raw + offset + limit:raw 模式下在 offset/limit 范围内 trim
        File.WriteAllText(Path.Combine(_dir, "trol.txt"), "  hello  \n  world  \n  foo  \n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "trol.txt", ["trim"] = true, ["raw"] = true, ["offset"] = 2, ["limit"] = 2 }, ctx, CancellationToken.None);

        Assert.Contains("world" + Environment.NewLine + "foo", output); // offset=2, limit=2
        Assert.DoesNotContain("hello", output); // offset 范围外
        Assert.DoesNotContain("  ", output); // 首尾空白被 trim
    }

    [Fact]
    public async Task ReadFile_Raw_ReturnsUnmodifiedContent()
    {
        // raw=true:不带行号、不截断、不显示编码提示，原样输出
        _ = TextUtil.EstimateTokens("");
        var gbk = System.Text.Encoding.GetEncoding("GB18030");
        File.WriteAllBytes(Path.Combine(_dir, "raw.txt"), gbk.GetBytes("中文\n" + new string('X', 3000) + "\nend\n"));
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "raw.txt", ["raw"] = true }, ctx, CancellationToken.None);

        Assert.Contains("中文", output);
        Assert.Contains(new string('X', 3000), output); // 不截断
        Assert.DoesNotContain("1\t", output); // 无行号
        Assert.DoesNotContain("（编码", output); // 无编码提示
    }

    [Fact]
    public async Task ReadFile_Raw_WithHead_StillLimitsLines()
    {
        // raw=true + head:仍按 head 限制行数，只是不显示行号/不截断
        File.WriteAllText(Path.Combine(_dir, "rh.txt"), "a\nb\nc\nd\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "rh.txt", ["raw"] = true, ["head"] = 2 }, ctx, CancellationToken.None);

        Assert.Contains("a" + Environment.NewLine + "b", output); // 无行号，无尾换行（head 不补）
    }

    [Fact]
    public async Task ReadFile_SkipEmpty_WithMaxLineLength_StillHidesBlanks()
    {
        // skip_empty + max_line_length 组合:空行被隐藏,长行仍按 max_line_length 截断
        File.WriteAllText(Path.Combine(_dir, "combo.txt"), "A\n\n" + new string('X', 5000) + "\nB\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "combo.txt", ["skip_empty"] = true, ["max_line_length"] = 50 }, ctx, CancellationToken.None);

        Assert.Contains("A", output);
        Assert.Contains("B", output);
        Assert.DoesNotContain("  ", output); // 空行不输出
        Assert.Contains("…", output); // 长行被截断
    }

    [Fact]
    public async Task ReadFile_SkipEmpty_WithNoLineNumbers_HidesBlankLines()
    {
        // skip_empty + no_line_numbers:空行不输出,且无行号前缀
        File.WriteAllText(Path.Combine(_dir, "nb.txt"), "a\n\nb\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "nb.txt", ["skip_empty"] = true, ["no_line_numbers"] = true }, ctx, CancellationToken.None);

        Assert.Contains("a" + Environment.NewLine + "b", output); // 无行号,无空行
    }

    [Fact]
    public async Task ReadFile_SkipEmpty_HidesBlankLines()
    {
        // skip_empty=true:空行不输出，但行号仍按原文件保持
        File.WriteAllText(Path.Combine(_dir, "blank.txt"), "a\n\nb\n\nc\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "blank.txt", ["skip_empty"] = true }, ctx, CancellationToken.None);

        Assert.Contains("1\ta", output);
        Assert.Contains("3\tb", output); // 行号仍按原文件
        Assert.DoesNotContain("2\t", output); // 空行不输出
        Assert.DoesNotContain("4\t", output);
    }

    [Fact]
    public async Task ReadFile_MaxLineLength_NegativeValue_FallsBackToDefault()
    {
        // 负值 max_line_length 应回退到默认 2000，不引发异常
        var path = Path.Combine(_dir, "neg.txt");
        File.WriteAllText(path, new string('A', 3000) + "\nshort\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "neg.txt", ["max_line_length"] = -1 }, ctx, CancellationToken.None);

        Assert.Contains("…", output); // 使用默认截断
        Assert.Contains("short", output);
    }

    [Fact]
    public async Task ReadFile_NoEncodingNote_SuppressesEncodingHint()
    {
        // no_encoding_note=true：已知编码时抑制开头的"（编码: …）"提示，减少输出噪音
        _ = TextUtil.EstimateTokens("");
        var gbk = System.Text.Encoding.GetEncoding("GB18030");
        File.WriteAllBytes(Path.Combine(_dir, "ansi.txt"), gbk.GetBytes("中文内容"));
        var tool = new ReadFileTool();
        var ctx = new AgentContext { Config = new AgentConfig(), Workspace = new Workspace(_dir) };

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "ansi.txt", ["no_encoding_note"] = true }, ctx, CancellationToken.None);

        Assert.Contains("中文内容", output);
        Assert.DoesNotContain("GBK", output); // 不应出现编码提示
    }

    [Fact]
    public async Task EditFile_CaseInsensitive_MatchesDifferentCase()
    {
        // case_insensitive=true:old_string 忽略大小写匹配，new_string 按原样写入
        File.WriteAllText(Path.Combine(_dir, "ci.txt"), "Hello World\n");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var result = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "ci.txt", ["old_string"] = "hello world", ["new_string"] = "HI", ["case_insensitive"] = true }, ctx, CancellationToken.None);

        Assert.Equal("HI\n", File.ReadAllText(Path.Combine(_dir, "ci.txt")));
        Assert.Contains("1 处", result);
    }

    [Fact]
    public async Task EditFile_CaseInsensitive_NoMatch_Throws()
    {
        // case_insensitive=true 但完全不匹配：仍报未找到
        File.WriteAllText(Path.Combine(_dir, "ci2.txt"), "Hello World\n");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() => tool.ExecuteAsync(
            new JsonObject { ["path"] = "ci2.txt", ["old_string"] = "zzz", ["new_string"] = "HI", ["case_insensitive"] = true }, ctx, CancellationToken.None));

        Assert.Contains("未找到", ex.Message);
    }

    [Fact]
    public async Task EditFile_CaseInsensitive_ReplaceAll_ReplacesAllOccurrences()
    {
        // case_insensitive=true + replace_all=true:所有大小写变体都被替换
        File.WriteAllText(Path.Combine(_dir, "cia.txt"), "Cat\ncat\nCAT\n");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var result = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "cia.txt", ["old_string"] = "cat", ["new_string"] = "dog", ["case_insensitive"] = true, ["replace_all"] = true }, ctx, CancellationToken.None);

        Assert.Equal("dog\ndog\ndog\n", File.ReadAllText(Path.Combine(_dir, "cia.txt")));
        Assert.Contains("3 处", result);
    }

    [Fact]
    public async Task EditFile_CaseInsensitive_PreservesOriginalCaseInResult()
    {
        // case_insensitive=true:匹配时忽略大小写，但替换后文件内容按 new_string 写入
        File.WriteAllText(Path.Combine(_dir, "cip.txt"), "Hello World\n");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "cip.txt", ["old_string"] = "hello world", ["new_string"] = "HI", ["case_insensitive"] = true }, ctx, CancellationToken.None);

        Assert.Equal("HI\n", File.ReadAllText(Path.Combine(_dir, "cip.txt")));
    }

    [Fact]
    public async Task EditFile_CaseInsensitive_WithCrlf_PreservesLineEndings()
    {
        // case_insensitive=true + CRLF 文件：匹配忽略大小写，保留 CRLF
        File.WriteAllText(Path.Combine(_dir, "cicrlf.txt"), "Hello World\r\n");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "cicrlf.txt", ["old_string"] = "hello world", ["new_string"] = "HI", ["case_insensitive"] = true }, ctx, CancellationToken.None);

        Assert.Equal("HI\r\n", File.ReadAllText(Path.Combine(_dir, "cicrlf.txt")));
    }

    [Fact]
    public async Task EditFile_CaseInsensitive_ReplaceAll_WithCrlf_Works()
    {
        // case_insensitive=true + replace_all=true + CRLF 文件：所有变体被替换，保留 CRLF
        File.WriteAllText(Path.Combine(_dir, "cicrlf2.txt"), "Cat\r\ncat\r\nCAT\r\n");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var result = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "cicrlf2.txt", ["old_string"] = "cat", ["new_string"] = "dog", ["case_insensitive"] = true, ["replace_all"] = true }, ctx, CancellationToken.None);

        Assert.Equal("dog\r\ndog\r\ndog\r\n", File.ReadAllText(Path.Combine(_dir, "cicrlf2.txt")));
        Assert.Contains("3 处", result);
    }

    [Fact]
    public async Task EditFile_PreservesUtf8Bom()
    {
        // 回归：改写曾丢 BOM——PowerShell 5.1 等工具靠 BOM 识别 UTF-8，丢掉后中文变乱码
        var path = Path.Combine(_dir, "bom.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("hello BOM world")]);
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "bom.txt", ["old_string"] = "BOM", ["new_string"] = " бом" },
            ctx, CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "改写后应保留 UTF-8 BOM");
        var text = System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.Contains(" бом", text); // 替换内容也正确写入
    }

    [Fact]
    public async Task EditFile_GbkFile_StaysGbkEncoded()
    {
        // 回归：edit_file 曾把 GBK 文件重编码为 UTF-8——内容可读但编码被静默转换，
        // 依赖 ANSI 编码的旧工具链（老编译器/批处理）会读到乱码
        _ = TextUtil.EstimateTokens(""); // 注册 GB18030 代码页
        var path = Path.Combine(_dir, "legacy-gbk.txt");
        var gbk = System.Text.Encoding.GetEncoding("GB18030");
        File.WriteAllBytes(path, gbk.GetBytes("第一行旧内容\n第二行旧内容\n"));
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "legacy-gbk.txt", ["old_string"] = "第一行旧内容", ["new_string"] = "第一行新内容" },
            ctx, CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal("第一行新内容\n第二行旧内容\n", gbk.GetString(bytes)); // 仍是 GBK，可原样解码
        // 且不能是合法 UTF-8 编码的同一文本（证明真的按 GB18030 写回）
        Assert.Throws<System.Text.DecoderFallbackException>(
            () => new System.Text.UTF8Encoding(false, true).GetString(bytes));
    }

    [Fact]
    public async Task Undo_AfterGbkEdit_RestoresGbkEncoding()
    {
        // 撤销条目记录原编码：GBK 文件改错后 /undo，恢复的仍是 GBK（而非被转成 UTF-8）
        _ = TextUtil.EstimateTokens(""); // 注册 GB18030 代码页
        var gbk = System.Text.Encoding.GetEncoding("GB18030");
        var path = Path.Combine(_dir, "u-gbk.txt");
        File.WriteAllBytes(path, gbk.GetBytes("旧内容"));
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);
        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "u-gbk.txt", ["old_string"] = "旧内容", ["new_string"] = "新内容" },
            ctx, CancellationToken.None);
        Assert.Equal("新内容", gbk.GetString(File.ReadAllBytes(path))); // 编辑后仍 GBK

        Assert.NotNull(ctx.Undo.TryUndo());

        var restored = File.ReadAllBytes(path);
        Assert.Equal("旧内容", gbk.GetString(restored)); // 撤销后仍是 GBK
        Assert.Throws<System.Text.DecoderFallbackException>(() =>
            new System.Text.UTF8Encoding(false, true).GetString(restored)); // 不是 UTF-8
    }

    [Fact]
    public async Task ReadFile_PlainUtf8_NoEncodingNote()
    {
        // 纯 UTF-8 文件不应被附编码提示（避免噪声，也不破坏现有逐字断言）
        File.WriteAllText(Path.Combine(_dir, "u.txt"), "hello\nworld\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(new JsonObject { ["path"] = "u.txt" }, ctx, CancellationToken.None);
        Assert.DoesNotContain("编码", output);
        Assert.Contains("hello", output);
    }

    [Fact]
    public async Task ReadFile_GbkFile_ShowsEncodingNote()
    {
        // 回归：非 UTF-8（GBK/ANSI）文件必须显式标注编码，否则模型可能误当 UTF-8 去改写
        _ = TextUtil.EstimateTokens(""); // 注册 GB18030 代码页
        var gbk = System.Text.Encoding.GetEncoding("GB18030");
        var path = Path.Combine(_dir, "g.txt");
        File.WriteAllBytes(path, gbk.GetBytes("第一行旧编码\n第二行\n"));
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(new JsonObject { ["path"] = "g.txt" }, ctx, CancellationToken.None);
        Assert.Contains("编码: GBK", output);
        Assert.Contains("第一行旧编码", output); // 内容仍正确解码
    }

    [Fact]
    public async Task ReadFile_Hash_AppendsSha256()
    {
        // hash=true:输出末尾附加 SHA256 哈希，可用于校验文件完整性
        File.WriteAllText(Path.Combine(_dir, "hash.txt"), "hello world");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "hash.txt", ["hash"] = true }, ctx, CancellationToken.None);

        Assert.Contains("sha256:", output);
        // 计算预期哈希并验证
        using var sha = System.Security.Cryptography.SHA256.Create();
        var expected = Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("hello world")));
        Assert.Contains(expected, output);
    }

    [Fact]
    public async Task ReadFile_EncodingGbk_DecodesCorrectly()
    {
        // encoding=gbk:强制以 GBK 解码，避免自动检测误判
        _ = TextUtil.EstimateTokens(""); // 触发静态构造，注册 GB18030 代码页
        var gbk = System.Text.Encoding.GetEncoding("GB18030");
        File.WriteAllBytes(Path.Combine(_dir, "gbk.txt"), gbk.GetBytes("中文内容"));
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "gbk.txt", ["encoding"] = "gbk" }, ctx, CancellationToken.None);

        Assert.Contains("中文内容", output);
    }

    [Fact]
    public async Task ReadFile_EncodingUtf8Bom_DecodesCorrectly()
    {
        // encoding=utf8-bom:强制以 UTF-8 BOM 解码
        var bom = new System.Text.UTF8Encoding(true);
        File.WriteAllBytes(Path.Combine(_dir, "bom.txt"), [0xEF, 0xBB, 0xBF, .. bom.GetBytes("bom content")]);
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "bom.txt", ["encoding"] = "utf8-bom" }, ctx, CancellationToken.None);

        Assert.Contains("bom content", output);
    }

    [Fact]
    public async Task ReadFile_EncodingUnsupported_Throws()
    {
        // encoding 不识别：返回清晰的错误而非崩溃
        File.WriteAllText(Path.Combine(_dir, "plain.txt"), "hello");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(new JsonObject { ["path"] = "plain.txt", ["encoding"] = "utf-16" }, ctx, CancellationToken.None));

        Assert.Contains("不支持的编码", ex.Message);
    }

}
