using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class InterfaceContractReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-iface-" + Guid.NewGuid().ToString("N"));

    public InterfaceContractReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Iface_FindsUnimplementedMember()
    {
        WriteSource(
            "interface IRepo", "{", "    void Save(string a);", "}",
            "class Repo : IRepo", "{", "    public void Delete(string a) { }", "}");
        var result = await new InterfaceContractReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("接口契约报告: 1 个 .cs 文件", result);
        Assert.Contains("接口成员未实现", result);
        Assert.Contains("IRepo.Save", result);
    }

    [Fact]
    public async Task Iface_ParameterTypeChangeIsCaught()
    {
        // 关键：实现把参数类型改了，方法名还在——人眼扫过去很容易漏
        WriteSource(
            "interface IRepo", "{", "    void Save(string a);", "}",
            "class Repo : IRepo", "{", "    public void Save(int a) { }", "}");
        var result = await new InterfaceContractReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("接口成员未实现", result);
    }

    [Fact]
    public async Task Iface_RenamedParameterIsNotDrift()
    {
        // 改了参数名不该算漂移——契约说的是类型不是名字
        WriteSource(
            "interface IRepo", "{", "    void Save(string path);", "}",
            "class Repo : IRepo", "{", "    public void Save(string file) { }", "}");
        var result = await new InterfaceContractReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("接口成员未实现", result);
    }

    [Fact]
    public async Task Iface_FullyImplementedIsClean()
    {
        WriteSource(
            "interface IRepo", "{", "    void Save(string a);", "    int Count();", "}",
            "class Repo : IRepo", "{",
            "    public override void Save(string a) { }",
            "    public override int Count() => 0;", "}");
        var result = await new InterfaceContractReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现接口契约漂移", result);
    }

    [Fact]
    public void Normalize_ComparesTypesNotNames()
    {
        Assert.Equal(InterfaceContractReportTool.Normalize("string a, int b"),
                     InterfaceContractReportTool.Normalize("string x, int y = 3"));
        Assert.NotEqual(InterfaceContractReportTool.Normalize("string a"),
                        InterfaceContractReportTool.Normalize("int a"));
    }

    [Fact]
    public async Task Iface_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new InterfaceContractReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new InterfaceContractReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new InterfaceContractReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
