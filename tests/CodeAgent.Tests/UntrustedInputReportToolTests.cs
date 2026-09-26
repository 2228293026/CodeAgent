using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class UntrustedInputReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-untrusted-" + Guid.NewGuid().ToString("N"));

    public UntrustedInputReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteSource(params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, "A.cs"), string.Join("\n", lines));

    [Fact]
    public async Task Untrusted_FindsShellInjection()
    {
        WriteSource(
            "class A", "{", "    void M(string userCommand)", "    {",
            "        Process.Start(\"cmd\", $\"/c dir {userCommand}\");", "    }", "}");
        var result = await new UntrustedInputReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("不可信输入报告: 1 个 .cs 文件", result);
        Assert.Contains("命令/SQL 拼接", result);
        Assert.Contains("userCommand", result);
    }

    [Fact]
    public async Task Untrusted_FindsUnvalidatedPath()
    {
        WriteSource(
            "class A", "{", "    void M(string path)", "    {", "        File.ReadAllText(path);", "    }", "}");
        var result = await new UntrustedInputReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未校验的外部输入使用", result);
        Assert.Contains("根目录校验", result);
    }

    [Fact]
    public async Task Untrusted_FindsSqlConcatenation()
    {
        WriteSource(
            "class A", "{", "    void M(string userId)", "    {",
            "        var sql = $\"SELECT * FROM t WHERE id = {userId}\";", "    }", "}");
        var result = await new UntrustedInputReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未校验的外部输入使用", result);
        Assert.Contains("参数化", result);
    }

    [Fact]
    public async Task Untrusted_LocalVariablesAreNotFlagged()
    {
        // 关键误报防线：本地常量拼进命令是安全的，不该报
        WriteSource(
            "class A", "{", "    void M()", "    {",
            "        var cacheDir = \"build\";",
            "        Process.Start(\"cmd\", $\"/c dir {cacheDir}\");", "    }", "}");
        var result = await new UntrustedInputReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现不可信输入问题", result);
    }

    [Fact]
    public void TheUntrustedHeuristicIsDeliberatelyNarrow()
    {
        // 只认明确来源：猜宽了会把所有本地变量都报一遍，报告立刻没人看
        Assert.True(UntrustedInputReportTool.IsUntrusted("userInput"));
        Assert.True(UntrustedInputReportTool.IsUntrusted("filePath"));
        Assert.True(UntrustedInputReportTool.IsUntrusted("args"));
        Assert.False(UntrustedInputReportTool.IsUntrusted("count"));
        Assert.False(UntrustedInputReportTool.IsUntrusted("result"));
    }

    [Fact]
    public async Task Untrusted_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new UntrustedInputReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new UntrustedInputReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UntrustedInputReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
