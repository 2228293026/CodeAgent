using System;
using System.IO;
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
}
