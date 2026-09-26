using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class LineEndingMixedReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-eol-" + Guid.NewGuid().ToString("N"));

    public LineEndingMixedReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteRaw(string name, byte[] bytes) =>
        File.WriteAllBytes(Path.Combine(_dir, name), bytes);

    private static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task LineEnding_FindsMixedAcrossFiles()
    {
        WriteRaw("A.cs", Utf8("int a = 1;\r\n"));
        WriteRaw("B.cs", Utf8("int b = 2;\n"));
        var result = await new LineEndingMixedReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("换行符报告: 2 个文本文件", result);
        Assert.Contains("仓库换行混杂", result);
        Assert.Contains("CRLF 文件: 1", result);
        Assert.Contains("LF 文件: 1", result);
    }

    [Fact]
    public async Task LineEnding_FindsMixedInsideOneFile()
    {
        WriteRaw("A.cs", Utf8("int a = 1;\r\nint b = 2;\n"));
        var result = await new LineEndingMixedReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("文件内混用: 1", result);
        Assert.Contains("A.cs", result);
    }

    [Fact]
    public async Task LineEnding_FindsMissingTrailingNewline()
    {
        WriteRaw("A.cs", Utf8("int a = 1;"));
        var result = await new LineEndingMixedReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("末尾无换行: 1", result);
        Assert.Contains("No newline at end of file", result);
    }

    [Fact]
    public async Task LineEnding_ConsistentLfIsClean()
    {
        WriteRaw("A.cs", Utf8("int a = 1;\nint b = 2;\n"));
        var result = await new LineEndingMixedReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现换行符问题", result);
    }

    [Fact]
    public async Task LineEnding_BomAndBareCrAreHandled()
    {
        // UTF-8 BOM 不该被当成内容；单独的 CR 算 LF 类（老 Mac 换行）
        WriteRaw("A.cs", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8("int a = 1;\r")).ToArray());
        WriteRaw("B.cs", Utf8("int b = 2;\r"));
        var result = await new LineEndingMixedReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("LF 文件: 2", result);
        Assert.DoesNotContain("文件内混用", result);
    }

    [Fact]
    public async Task LineEnding_BinaryFilesAreIgnored()
    {
        WriteRaw("A.bin", new byte[] { 0x0D, 0x0A, 0x00, 0x0D, 0x0A });
        var result = await new LineEndingMixedReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到可检查的文本文件", result);
    }

    [Fact]
    public async Task LineEnding_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new LineEndingMixedReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到可检查的文本文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new LineEndingMixedReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteRaw("A.cs", Utf8("int a = 1;\n"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LineEndingMixedReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
