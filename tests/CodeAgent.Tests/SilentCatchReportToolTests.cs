using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class SilentCatchReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-silent-" + Guid.NewGuid().ToString("N"));

    public SilentCatchReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Silent_FindsEmptyCatch()
    {
        WriteSource("class A", "{", "    void M()", "    {", "        try { N(); }", "        catch (Exception) { }", "    }", "    void N() { }", "}");
        var result = await new SilentCatchReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("静默失败报告: 1 个 .cs 文件", result);
        Assert.Contains("空 catch", result);
    }

    [Fact]
    public async Task Silent_FindsDefaultReturn()
    {
        WriteSource(
            "class A", "{", "    int M()", "    {", "        try { return N(); }",
            "        catch (IOException e)", "        {", "            return -1;", "        }", "    }", "    int N() => 0;", "}");
        var result = await new SilentCatchReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("catch 后返回默认值", result);
        Assert.Contains("失败被伪装成成功", result);
    }

    [Fact]
    public async Task Silent_FindsUnusedVariable()
    {
        WriteSource(
            "class A", "{", "    void M()", "    {", "        try { N(); }",
            "        catch (Exception ex)", "        {", "            Cleanup();", "        }", "    }",
            "    void N() { }", "    void Cleanup() { }", "}");
        var result = await new SilentCatchReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("catch 变量未使用", result);
        Assert.Contains("ex", result);
    }

    [Fact]
    public async Task Silent_LoggingOrRethrowIsFine()
    {
        WriteSource(
            "class A", "{", "    int M()", "    {", "        try { return N(); }",
            "        catch (IOException e)", "        {", "            Log(e);", "            throw;", "        }", "    }",
            "    int N() => 0;", "    void Log(Exception e) { }", "}");
        var result = await new SilentCatchReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("catch 后返回默认值", result);
        Assert.DoesNotContain("catch 变量未使用", result);
    }

    [Fact]
    public async Task Silent_UnderscoreVariableIsNotReported()
    {
        WriteSource(
            "class A", "{", "    void M()", "    {", "        try { N(); }",
            "        catch (Exception _)", "        {", "            Cleanup();", "        }", "    }",
            "    void N() { }", "    void Cleanup() { }", "}");
        var result = await new SilentCatchReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("catch 变量未使用", result);
    }

    [Fact]
    public async Task Silent_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new SilentCatchReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new SilentCatchReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SilentCatchReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
