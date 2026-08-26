using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public class ApplyPatchToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-applypatch-" + Guid.NewGuid().ToString("N"));

    public ApplyPatchToolTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private AgentContext MakeContext() => new() { Config = new AgentConfig(), Workspace = new Workspace(_dir) };

    private async Task<string> Apply(string patch, string? path = null, bool validateOnly = false)
    {
        var tool = new ApplyPatchTool();
        var ctx = MakeContext();
        var args = new JsonObject { ["patch"] = patch };
        if (path is not null) args["path"] = path;
        if (validateOnly) args["validate_only"] = true;
        return await tool.ExecuteAsync(args, ctx, CancellationToken.None);
    }

    [Fact]
    public async Task Apply_SimpleLineReplacement_UpdatesFile()
    {
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "alpha\nbeta\ngamma\n");
        var patch = "@@ -1,3 +1,3 @@\n alpha\n-beta\n+BETA\n gamma\n";

        var output = await Apply(patch, "f.txt");

        Assert.Contains("已应用 1 个 hunk", output);
        Assert.Equal("alpha\nBETA\ngamma\n", File.ReadAllText(Path.Combine(_dir, "f.txt")));
    }

    [Fact]
    public async Task Apply_AddAndRemoveLines_Works()
    {
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "a\nb\nc\n");
        // 删除第 2 行,同时在末尾新增 d
        var patch = "@@ -1,3 +1,3 @@\n a\n-b\n c\n+d\n";

        await Apply(patch, "f.txt");

        Assert.Equal("a\nc\nd\n", File.ReadAllText(Path.Combine(_dir, "f.txt")));
    }

    [Fact]
    public async Task Apply_MultipleHunks_SingleFile()
    {
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "l1\nl2\nl3\nl4\nl5\n");
        var patch = "@@ -1,1 +1,1 @@\n-l1\n+X1\n@@ -5,1 +5,1 @@\n-l5\n+X5\n";

        await Apply(patch, "f.txt");

        Assert.Equal("X1\nl2\nl3\nl4\nX5\n", File.ReadAllText(Path.Combine(_dir, "f.txt")));
    }

    [Fact]
    public async Task Apply_FileHeader_PicksPathFromPatch()
    {
        File.WriteAllText(Path.Combine(_dir, "x.txt"), "old\n");
        var patch = "--- a/x.txt\n+++ b/x.txt\n@@ -1 +1 @@\n-old\n+new\n";

        var output = await Apply(patch); // 不传 path,靠 +++ b/x.txt

        Assert.Contains("x.txt", output);
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(_dir, "x.txt")));
    }

    [Fact]
    public async Task Apply_MultipleFiles_InOnePatch()
    {
        File.WriteAllText(Path.Combine(_dir, "m1.txt"), "A\n");
        File.WriteAllText(Path.Combine(_dir, "m2.txt"), "B\n");
        var patch = "+++ b/m1.txt\n@@ -1 +1 @@\n-A\n+A1\n" +
                    "+++ b/m2.txt\n@@ -1 +1 @@\n-B\n+B1\n";

        var output = await Apply(patch);

        Assert.Contains("m1.txt", output);
        Assert.Contains("m2.txt", output);
        Assert.Equal("A1\n", File.ReadAllText(Path.Combine(_dir, "m1.txt")));
        Assert.Equal("B1\n", File.ReadAllText(Path.Combine(_dir, "m2.txt")));
    }

    [Fact]
    public async Task Apply_ContextMismatch_ThrowsAndLeavesFileUntouched()
    {
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "alpha\nbeta\ngamma\n");
        var patch = "@@ -1,3 +1,3 @@\n alpha\n-WRONG\n gamma\n";

        var ex = await Assert.ThrowsAsync<ToolException>(() => Apply(patch, "f.txt"));

        Assert.Contains("不匹配", ex.Message);
        Assert.Equal("alpha\nbeta\ngamma\n", File.ReadAllText(Path.Combine(_dir, "f.txt")));
    }

    [Fact]
    public async Task Apply_ValidateOnly_DoesNotWrite()
    {
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "alpha\nbeta\n");
        var patch = "@@ -1,2 +1,2 @@\n alpha\n-beta\n+XX\n";

        var output = await Apply(patch, "f.txt", validateOnly: true);

        Assert.Contains("验证通过", output);
        Assert.Contains("未写盘", output);
        Assert.Equal("alpha\nbeta\n", File.ReadAllText(Path.Combine(_dir, "f.txt")));
    }

    [Fact]
    public async Task Apply_EmptyPatch_Throws()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => Apply("   "));
        Assert.Contains("patch", ex.Message);
    }

    [Fact]
    public async Task Apply_CrlfFile_KeepsCrlf()
    {
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "l1\r\nl2\r\nl3\r\n");
        var patch = "@@ -1,3 +1,3 @@\n l1\n-l2\n+N2\n l3\n";

        await Apply(patch, "f.txt");

        Assert.Equal("l1\r\nN2\r\nl3\r\n", File.ReadAllText(Path.Combine(_dir, "f.txt")));
    }

    [Fact]
    public async Task Apply_NoLeadingNewlineOnNewHunk_PreservesEnding()
    {
        // 原文件以换行结尾:应用后仍保持带尾换行(避免把文件改得不带换行)
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "a\nb\n");
        var patch = "@@ -1,2 +1,2 @@\n a\n-b\n+B\n";

        await Apply(patch, "f.txt");

        Assert.Equal("a\nB\n", File.ReadAllText(Path.Combine(_dir, "f.txt")));
    }
}
