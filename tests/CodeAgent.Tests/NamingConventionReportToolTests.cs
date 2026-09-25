using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class NamingConventionReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-naming-" + Guid.NewGuid().ToString("N"));

    public NamingConventionReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteSource(string name, params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, name), string.Join("\n", lines));

    [Fact]
    public async Task NamingConventionReport_FlagsFileTypeMismatch()
    {
        WriteSource("ReadFileTool.cs", "internal sealed class SomethingElse { }");
        var result = await new NamingConventionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("命名规范报告: 1 个 .cs 文件", result);
        Assert.Contains("文件名与类型名不一致: 1", result);
        Assert.Contains("SomethingElse", result);
    }

    [Fact]
    public async Task NamingConventionReport_FlagsUnderscoreFileName()
    {
        WriteSource("my_helper.cs", "internal class MyHelper { }");
        var result = await new NamingConventionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("非 PascalCase 文件名: 1", result);
        Assert.Contains("my_helper.cs", result);
    }

    [Fact]
    public async Task NamingConventionReport_FlagsVagueTypeName()
    {
        WriteSource("StringHelper.cs", "internal static class StringHelper { }");
        var result = await new NamingConventionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("含义模糊的类型名: 1", result);
        Assert.Contains("以「Helper」结尾", result);
        Assert.Contains("名字未说明职责", result);
    }

    [Fact]
    public async Task NamingConventionReport_CleanFileHasNoIssues()
    {
        WriteSource("FileReaderTool.cs", "internal sealed class FileReaderTool { }");
        var result = await new NamingConventionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现命名规范问题", result);
    }

    [Fact]
    public async Task NamingConventionReport_MatchesPartialTypeName()
    {
        // 部分类：文件名与类型名一致即通过
        WriteSource("UndoManager.cs", "public sealed partial class UndoManager { }", "public sealed partial class UndoManager { }");
        var result = await new NamingConventionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("文件名与类型名不一致", result);
    }

    [Fact]
    public async Task NamingConventionReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new NamingConventionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new NamingConventionReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("A.cs", "class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NamingConventionReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
