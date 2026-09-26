using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class RetrySafetyReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-retry-" + Guid.NewGuid().ToString("N"));

    public RetrySafetyReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Retry_FindsFixedIntervalRetry()
    {
        WriteSource(
            "class A", "{", "    void M()", "    {", "        while (true)", "        {",
            "            try { Call(); }", "            catch (Exception) { }",
            "            Thread.Sleep(1000);", "        }", "    }", "    void Call() { }", "}");
        var result = await new RetrySafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("重试安全报告: 1 个 .cs 文件", result);
        Assert.Contains("固定间隔重试（无退避）", result);
        Assert.Contains("重试没有次数上限", result);
    }

    [Fact]
    public async Task Retry_FindsNonIdempotentOperation()
    {
        WriteSource(
            "class A", "{", "    void M()", "    {", "        for (int attempt = 0; attempt < 3; attempt++)", "        {",
            "            try", "            {",
            "                File.WriteAllText(\"a.txt\", \"x\");",
            "            }", "            catch (IOException) { }",
            "            Thread.Sleep(attempt * 200);", "        }", "    }", "}");
        var result = await new RetrySafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("重试里包含非幂等操作", result);
        Assert.Contains("幂等键", result);
    }

    [Fact]
    public async Task Retry_BoundedBackoffRetryIsClean()
    {
        WriteSource(
            "class A", "{", "    void M()", "    {", "        for (int attempt = 0; attempt < 3; attempt++)", "        {",
            "            try { Get(); }", "            catch (HttpRequestException) { }",
            "            Thread.Sleep(attempt * 200);", "        }", "    }", "    void Get() { }", "}");
        var result = await new RetrySafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现重试安全问题", result);
    }

    [Fact]
    public void TheNonIdempotentListIsCurated()
    {
        Assert.Contains("File.WriteAllText", RetrySafetyReportTool.NonIdempotent);
        Assert.Contains("PostAsync", RetrySafetyReportTool.NonIdempotent);
        // 读操作不该在里面：重试读是安全的
        Assert.DoesNotContain("File.ReadAllText", RetrySafetyReportTool.NonIdempotent);
    }

    [Fact]
    public async Task Retry_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new RetrySafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new RetrySafetyReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RetrySafetyReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
