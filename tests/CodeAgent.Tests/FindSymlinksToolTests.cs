using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class FindSymlinksToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-symlinks-" + Guid.NewGuid().ToString("N"));

    public FindSymlinksToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task FindSymlinks_ReportsFileLinkAndBrokenState()
    {
        var target = Path.Combine(_dir, "target.txt");
        File.WriteAllText(target, "content");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_dir, "link.txt"), target);
            File.CreateSymbolicLink(Path.Combine(_dir, "broken.txt"), Path.Combine(_dir, "missing.txt"));
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        catch (PlatformNotSupportedException) { return; }

        var result = await new FindSymlinksTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("link.txt", result);
        Assert.Contains("target.txt", result);
        Assert.Contains("broken.txt", result);
        Assert.Contains("broken", result);
    }

    [Fact]
    public async Task FindSymlinks_DoesNotFollowDirectoryLink()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "real", "nested"));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_dir, "linkdir"), Path.Combine(_dir, "real"));
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        catch (PlatformNotSupportedException) { return; }
        File.CreateSymbolicLink(Path.Combine(_dir, "real", "nested", "inside.txt"), Path.Combine(_dir, "outside.txt"));
        var result = await new FindSymlinksTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("linkdir", result);
        Assert.Contains("inside.txt", result);
    }

    [Fact]
    public async Task FindSymlinks_ReportsResultCap()
    {
        var target = Path.Combine(_dir, "target.txt");
        File.WriteAllText(target, "content");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_dir, "a-link.txt"), target);
            File.CreateSymbolicLink(Path.Combine(_dir, "b-link.txt"), target);
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        catch (PlatformNotSupportedException) { return; }
        var result = await new FindSymlinksTool().ExecuteAsync(
            new JsonObject { ["max_results"] = 1 }, Context(), CancellationToken.None);
        Assert.Contains("max_results=1", result);
    }

    [Fact]
    public async Task FindSymlinks_OutsidePathThrows()
    {
        await Assert.ThrowsAsync<ToolException>(() => new FindSymlinksTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task FindSymlinks_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FindSymlinksTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
