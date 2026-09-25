using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class NormalizeLineEndingsToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-normalize-" + Guid.NewGuid().ToString("N"));

    public NormalizeLineEndingsToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task NormalizeLineEndings_ConvertsCrlfToLfAndCanUndo()
    {
        var path = Path.Combine(_dir, "a.txt");
        File.WriteAllText(path, "one\r\ntwo\r\n");
        var context = Context();
        var result = await new NormalizeLineEndingsTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("a.txt"), ["line_ending"] = "lf" }, context, CancellationToken.None);
        Assert.Contains("已变更 1 个", result);
        Assert.Equal("one\ntwo\n", File.ReadAllText(path));
        Assert.NotNull(context.Undo.TryUndo());
        Assert.Equal("one\r\ntwo\r\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task NormalizeLineEndings_ConvertsLfToCrlf()
    {
        var path = Path.Combine(_dir, "a.txt");
        File.WriteAllText(path, "one\ntwo\n");
        var result = await new NormalizeLineEndingsTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("a.txt"), ["line_ending"] = "crlf" }, Context(), CancellationToken.None);
        Assert.Contains("CRLF", result);
        Assert.Equal("one\r\ntwo\r\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task NormalizeLineEndings_DryRunDoesNotWriteOrUndo()
    {
        var path = Path.Combine(_dir, "a.txt");
        File.WriteAllText(path, "one\r\ntwo\r\n");
        var context = Context();
        var result = await new NormalizeLineEndingsTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("a.txt"), ["dry_run"] = true }, context, CancellationToken.None);
        Assert.Contains("dry-run", result);
        Assert.Equal("one\r\ntwo\r\n", File.ReadAllText(path));
        Assert.Null(context.Undo.TryUndo());
    }

    [Fact]
    public async Task NormalizeLineEndings_CreatesBackupAndHandlesMissing()
    {
        var path = Path.Combine(_dir, "a.txt");
        File.WriteAllText(path, "one\r\n");
        var result = await new NormalizeLineEndingsTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("a.txt", "missing.txt"), ["backup"] = true, ["missing_ok"] = true },
            Context(), CancellationToken.None);
        Assert.Contains("跳过 1 个", result);
        Assert.Equal("one\r\n", File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public async Task NormalizeLineEndings_RejectsInvalidStyle()
    {
        await Assert.ThrowsAsync<ToolException>(() => new NormalizeLineEndingsTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("a.txt"), ["line_ending"] = "cr" }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task NormalizeLineEndings_CanceledToken_StopsBeforeRead()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NormalizeLineEndingsTool().ExecuteAsync(
            new JsonObject { ["paths"] = new JsonArray("missing.txt") }, Context(), cts.Token));
    }
}
