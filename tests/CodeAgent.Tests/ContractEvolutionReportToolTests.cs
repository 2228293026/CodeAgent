using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ContractEvolutionReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-contract-" + Guid.NewGuid().ToString("N"));

    public ContractEvolutionReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Contract_FindsRequiredParameter()
    {
        WriteSource("class A", "{", "    public string M(string name)", "    {", "        return name;", "    }", "}");
        var result = await new ContractEvolutionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("契约演进报告: 1 个 .cs 文件", result);
        Assert.Contains("必需参数没有默认值", result);
        Assert.Contains("string name", result);
    }

    [Fact]
    public async Task Contract_DefaultedParameterIsNotItselfReported()
    {
        WriteSource("class A", "{", "    public string M(string name, int limit = 10)", "    {", "        return name;", "    }", "}");
        var result = await new ContractEvolutionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        // string name 确实没有默认值，该被报；带默认值的 limit 不该被报出来
        Assert.Contains("string name", result);
        Assert.DoesNotContain("int limit", result);
    }

    [Fact]
    public async Task Contract_FindsLeakedCollection()
    {
        WriteSource(
            "using System.Collections.Generic;", "class A", "{",
            "    List<int> items = new();",
            "    public List<int> Items()", "    {", "        return items;", "    }", "}");
        var result = await new ContractEvolutionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("返回内部集合引用", result);
    }

    [Fact]
    public async Task Contract_CopiedCollectionIsClean()
    {
        WriteSource(
            "using System.Collections.Generic;", "using System.Linq;", "class A", "{",
            "    List<int> items = new();",
            "    public List<int> Items()", "    {", "        return items.ToList();", "    }", "}");
        var result = await new ContractEvolutionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("返回内部集合引用", result);
    }

    [Fact]
    public void SplitParamsKeepsGenericAndTupleCommas()
    {
        // 直接 Split(',') 会把 Dictionary<string, int> map 和 (int, int) p 拆成碎片
        var parts = ContractEvolutionReportTool.SplitParams("Dictionary<string, int> map, (int a, int b) pair, int n = 1");
        Assert.Equal(3, parts.Count);
        Assert.Equal("Dictionary<string, int> map", parts[0]);
        Assert.Equal(" (int a, int b) pair", parts[1]);
        Assert.Equal(" int n = 1", parts[2]);
    }

    [Fact]
    public async Task Contract_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ContractEvolutionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ContractEvolutionReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ContractEvolutionReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
