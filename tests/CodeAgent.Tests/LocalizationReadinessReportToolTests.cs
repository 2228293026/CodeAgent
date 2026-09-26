using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class LocalizationReadinessReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-l10n-" + Guid.NewGuid().ToString("N"));

    public LocalizationReadinessReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task LocalizationReport_FindsHardcodedUserText()
    {
        WriteFile("A.cs", "class A", "{", "    void M() => Console.WriteLine(\"读取文件失败\");", "}");
        var result = await new LocalizationReadinessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("国际化准备度报告: 1 个 .cs 文件", result);
        Assert.Contains("用户文案硬编码中文: 1", result);
        Assert.Contains("读取文件失败", result);
    }

    [Fact]
    public async Task LocalizationReport_FindsHardcodedDateFormat()
    {
        WriteFile("A.cs", "class A", "{", "    string M() => DateTime.Now.ToString(\"yyyy/MM/dd\");", "}");
        var result = await new LocalizationReadinessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("日期格式硬编码: 1", result);
        Assert.Contains("区域化格式", result);
    }

    [Fact]
    public async Task LocalizationReport_NotesMissingResourceFile()
    {
        WriteFile("A.cs", "class A", "{", "    void M() => Console.WriteLine(\"提示\");", "}");
        var result = await new LocalizationReadinessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .resx 等本地化资源", result);
        Assert.Contains("尚未具备多语言能力", result);
    }

    [Fact]
    public async Task LocalizationReport_ResourceFileIsAcknowledged()
    {
        WriteFile("Strings.resx", "<root />");
        WriteFile("A.cs", "class A", "{", "    void M() => Console.WriteLine(\"提示\");", "}");
        var result = await new LocalizationReadinessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("已找到本地化资源", result);
        Assert.DoesNotContain("尚未具备多语言能力", result);
    }

    [Fact]
    public async Task LocalizationReport_CommentedTextIsNotFlagged()
    {
        WriteFile("A.cs", "class A", "{", "    // Console.WriteLine(\"注释里的中文\");", "}");
        var result = await new LocalizationReadinessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("用户文案硬编码中文", result);
    }

    [Fact]
    public async Task LocalizationReport_AsciiOnlyCodeIsClean()
    {
        WriteFile("A.cs", "class A", "{", "    int V => 3;", "}");
        var result = await new LocalizationReadinessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现国际化准备度问题", result);
    }

    [Fact]
    public async Task LocalizationReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new LocalizationReadinessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new LocalizationReadinessReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteFile("A.cs", "class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocalizationReadinessReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
