using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class CompareFilesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-compare-" + Guid.NewGuid().ToString("N"));

    public CompareFilesToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    [Fact]
    public async Task CompareFiles_ShowsUnifiedDiff()
    {
        File.WriteAllText(Path.Combine(_dir, "old.txt"), "one\ntwo\n");
        File.WriteAllText(Path.Combine(_dir, "new.txt"), "one\nchanged\n");

        var result = await new CompareFilesTool().ExecuteAsync(
            new JsonObject { ["left"] = "old.txt", ["right"] = "new.txt" },
            Context(), CancellationToken.None);

        Assert.Contains("--- a/old.txt → new.txt", result);
        Assert.Contains("- two", result);
        Assert.Contains("+ changed", result);
    }

    [Fact]
    public async Task CompareFiles_IdenticalFiles_ReturnsFriendlyMessage()
    {
        File.WriteAllText(Path.Combine(_dir, "same.txt"), "same\n");
        var result = await new CompareFilesTool().ExecuteAsync(
            new JsonObject { ["left"] = "same.txt", ["right"] = "same.txt" },
            Context(), CancellationToken.None);
        Assert.Contains("同一个文件", result);
    }

    [Fact]
    public async Task CompareFiles_CanIgnoreWhitespaceAndBlankLines()
    {
        File.WriteAllText(Path.Combine(_dir, "old.txt"), "  value  \n\n");
        File.WriteAllText(Path.Combine(_dir, "new.txt"), "value\n");

        var result = await new CompareFilesTool().ExecuteAsync(
            new JsonObject
            {
                ["left"] = "old.txt",
                ["right"] = "new.txt",
                ["ignore_whitespace"] = true,
                ["ignore_blank_lines"] = true,
            }, Context(), CancellationToken.None);

        Assert.Contains("内容相同", result);
        Assert.Contains("忽略规则", result);
    }

    [Fact]
    public async Task CompareFiles_MissingPath_ThrowsHelpfulError()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => new CompareFilesTool().ExecuteAsync(
            new JsonObject { ["left"] = "missing.txt", ["right"] = "missing2.txt" },
            Context(), CancellationToken.None));
        Assert.Contains("left 文件不存在", ex.Message);
    }

    [Fact]
    public async Task CompareFiles_CapsOutputAndMarksTruncation()
    {
        File.WriteAllText(Path.Combine(_dir, "old.txt"), string.Join("\n", Enumerable.Range(1, 20).Select(i => $"old{i}")));
        File.WriteAllText(Path.Combine(_dir, "new.txt"), string.Join("\n", Enumerable.Range(1, 20).Select(i => $"new{i}")));

        var result = await new CompareFilesTool().ExecuteAsync(
            new JsonObject { ["left"] = "old.txt", ["right"] = "new.txt", ["max_lines"] = 5 },
            Context(), CancellationToken.None);

        Assert.Contains("已截断", result);
        Assert.True(result.Split('\n').Length <= 7, result);
    }

    [Fact]
    public async Task CompareFiles_CanceledToken_StopsBeforeReading()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CompareFilesTool().ExecuteAsync(
            new JsonObject { ["left"] = "missing.txt", ["right"] = "missing2.txt" },
            Context(), cts.Token));
    }
}
