using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ArgumentSafetyReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-argsafe-" + Guid.NewGuid().ToString("N"));

    public ArgumentSafetyReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task ArgumentSafety_FindsArgumentUsedAsPath()
    {
        WriteSource("class A", "{", "    void M(string path) => System.IO.File.ReadAllText(path);", "}");
        var result = await new ArgumentSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("实参安全报告: 1 个 .cs 文件", result);
        Assert.Contains("入参直用路径", result);
        Assert.Contains("A.cs:3", result);
    }

    [Fact]
    public async Task ArgumentSafety_ValidatedPathIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(string path)",
            "    {",
            "        if (string.IsNullOrWhiteSpace(path))",
            "            throw new ArgumentException(\"path\");",
            "        var text = System.IO.File.ReadAllText(path);",
            "    }",
            "}");
        var result = await new ArgumentSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("入参直用路径", result);
    }

    [Fact]
    public async Task ArgumentSafety_FindsMissingGuard()
    {
        WriteSource("class A", "{", "    public string M(string s) => s.Substring(1);", "}");
        var result = await new ArgumentSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("缺参数校验", result);
    }

    [Fact]
    public async Task ArgumentSafety_GuardedMethodIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    public string M(string s)",
            "    {",
            "        if (s is null) throw new ArgumentNullException(nameof(s));",
            "        return s.Substring(1);",
            "    }",
            "}");
        var result = await new ArgumentSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("缺参数校验", result);
    }

    [Fact]
    public async Task ArgumentSafety_FindsInterpolatedCommand()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(string query)",
            "    {",
            "        var psi = new System.Diagnostics.ProcessStartInfo(\"git\")",
            "        {",
            "            Arguments = $\"log --grep={query}\",",
            "        };",
            "    }",
            "}");
        var result = await new ArgumentSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("命令未用 ArgumentList", result);
    }

    [Fact]
    public async Task ArgumentSafety_ArgumentListIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    void M(string query)",
            "    {",
            "        var psi = new System.Diagnostics.ProcessStartInfo(\"git\")",
            "        {",
            "            Arguments = $\"log --grep={query}\",",
            "        };",
            "        psi.ArgumentList.Add(\"log\");",
            "    }",
            "}");
        var result = await new ArgumentSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("命令未用 ArgumentList", result);
    }

    [Fact]
    public async Task ArgumentSafety_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new ArgumentSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现实参安全问题", result);
    }

    [Fact]
    public async Task ArgumentSafety_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ArgumentSafetyReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ArgumentSafetyReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ArgumentSafetyReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
