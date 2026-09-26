using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class EqualsGetHashCodeReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-eqhash-" + Guid.NewGuid().ToString("N"));

    public EqualsGetHashCodeReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task EqHash_FindsEqualsWithoutHash()
    {
        WriteSource(
            "class A", "{", "    public override bool Equals(object o)", "    {", "        return true;", "    }", "}");
        var result = await new EqualsGetHashCodeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Equals/GetHashCode 报告: 1 个 .cs 文件", result);
        Assert.Contains("重写 Equals 但漏了 GetHashCode", result);
        Assert.Contains("HashSet", result);
    }

    [Fact]
    public async Task EqHash_FindsHashWithoutEquals()
    {
        WriteSource(
            "class A", "{", "    public override int GetHashCode()", "    {", "        return 1;", "    }", "}");
        var result = await new EqualsGetHashCodeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("重写 GetHashCode 但漏了 Equals", result);
    }

    [Fact]
    public async Task EqHash_FindsMismatchedFields()
    {
        WriteSource(
            "class A", "{",
            "    public string Name;", "    public int Size;",
            "    public override bool Equals(object o)", "    {", "        return Name == (o as A)?.Name;", "    }",
            "    public override int GetHashCode()", "    {", "        return Size;", "    }", "}");
        var result = await new EqualsGetHashCodeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("两个方法依据的字段不一致", result);
    }

    [Fact]
    public async Task EqHash_ConsistentPairIsClean()
    {
        WriteSource(
            "class A", "{",
            "    public string Name;",
            "    public override bool Equals(object o)", "    {", "        return Name == (o as A)?.Name;", "    }",
            "    public override int GetHashCode()", "    {", "        return Name.GetHashCode();", "    }", "}");
        var result = await new EqualsGetHashCodeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 Equals/GetHashCode 契约问题", result);
    }

    [Fact]
    public async Task EqHash_TwoTypesDoNotBleedIntoEachOther()
    {
        // 关键回归点：类型 A 有 Equals+GetHashCode，类型 B 只有 Equals。
        // 若不按类型分块，B 的"漏 GetHashCode"会被 A 的存在掩盖。
        WriteSource(
            "class A", "{", "    public override bool Equals(object o) => true;", "    public override int GetHashCode() => 1;", "}",
            "class B", "{", "    public override bool Equals(object o) => true;", "}");
        var result = await new EqualsGetHashCodeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("重写 Equals 但漏了 GetHashCode: 1", result);
    }

    [Fact]
    public async Task EqHash_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new EqualsGetHashCodeReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new EqualsGetHashCodeReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EqualsGetHashCodeReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
