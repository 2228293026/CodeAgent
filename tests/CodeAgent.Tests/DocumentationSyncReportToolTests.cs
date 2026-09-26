using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DocumentationSyncReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-docsync-" + Guid.NewGuid().ToString("N"));

    public DocumentationSyncReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteFile(string name, params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, name), string.Join("\n", lines));

    [Fact]
    public async Task DocSyncReport_FindsUndocumentedPublicMember()
    {
        WriteFile("A.cs", "class A", "{", "    public int Value;", "}");
        var result = await new DocumentationSyncReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("文档同步报告: 1 个 .cs 文件", result);
        Assert.Contains("公开成员缺文档: 1", result);
        Assert.Contains("A.cs:3", result);
    }

    [Fact]
    public async Task DocSyncReport_DocumentedMemberIsFine()
    {
        WriteFile("A.cs", "class A", "{", "    /// <summary>说明。</summary>", "    public int Value;", "}");
        var result = await new DocumentationSyncReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("公开成员缺文档", result);
    }

    [Fact]
    public async Task DocSyncReport_FindsMissingReadmeEntry()
    {
        WriteFile("SampleTool.cs", "public class SampleTool", "{", "    public void Run() { }", "}");
        // README 里**不能**出现类名，否则测的就不是「漏登记」了
        File.WriteAllText(Path.Combine(_dir, "README.md"), "# 项目\n\n只列了别的工具。\n");
        var result = await new DocumentationSyncReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("README 未登记", result);
        Assert.Contains("SampleTool", result);
    }

    [Fact]
    public async Task DocSyncReport_RegisteredToolIsFine()
    {
        WriteFile("SampleTool.cs", "public class SampleTool", "{", "    public void Run() { }", "}");
        File.WriteAllText(Path.Combine(_dir, "README.md"), "# 项目\n\n- SampleTool.cs 工具\n");
        var result = await new DocumentationSyncReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("README 未登记", result);
    }

    [Fact]
    public async Task DocSyncReport_FindsCommentedOutCode()
    {
        WriteFile("A.cs", "class A", "{", "    // var old = Compute();", "}");
        var result = await new DocumentationSyncReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("注释掉的死代码", result);
    }

    [Fact]
    public async Task DocSyncReport_ProseCommentIsNotDeadCode()
    {
        WriteFile("A.cs", "class A", "{", "    // 这里返回总数，不是差额", "    public int V => 3;", "}");
        var result = await new DocumentationSyncReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("注释掉的死代码", result);
    }

    [Fact]
    public async Task DocSyncReport_NotesMissingReadmeFile()
    {
        WriteFile("A.cs", "class A", "{", "    int V => 3;", "}");
        var result = await new DocumentationSyncReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 README.md", result);
    }

    [Fact]
    public async Task DocSyncReport_CleanCodeHasNoIssues()
    {
        WriteFile("README.md", "# 项目");
        WriteFile("A.cs", "class A", "{", "    /// <summary>说明。</summary>", "    public int Value => 3;", "}");
        var result = await new DocumentationSyncReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现文档同步问题", result);
    }

    [Fact]
    public async Task DocSyncReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new DocumentationSyncReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new DocumentationSyncReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteFile("A.cs", "class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DocumentationSyncReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
