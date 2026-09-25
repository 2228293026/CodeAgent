using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class I18nStringReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-i18n-" + Guid.NewGuid().ToString("N"));

    public I18nStringReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    [Fact]
    public async Task I18nStringReport_CountsPerFileWithSamples()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), string.Join("\n",
            "throw new ToolException(\"目录不存在\");",
            "Console.WriteLine(\"已完成\");",
            "var name = \"ascii only\";"));
        var result = await new I18nStringReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("中文字符串报告: 2 处，分布在 1 个文件", result);
        Assert.Contains("a.cs: 2", result);
        Assert.Contains("「目录不存在」", result);
        Assert.DoesNotContain("ascii only", result);
    }

    [Fact]
    public async Task I18nStringReport_SamplesLimitAndMaxFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"),
            "var a=\"一\"; var b=\"二\"; var c=\"三\"; var d=\"四\";");
        File.WriteAllText(Path.Combine(_dir, "b.cs"), "var e=\"五\";");
        var result = await new I18nStringReportTool().ExecuteAsync(
            new JsonObject { ["samples"] = 1, ["max_files"] = 1 }, Context(), CancellationToken.None);
        // 只列 1 个文件（按数量降序，a.cs 有 4 条）
        Assert.Contains("中文字符串报告: 5 处，分布在 2 个文件", result);
        Assert.Contains("a.cs: 4", result);
        Assert.DoesNotContain("b.cs", result);
    }

    [Fact]
    public async Task I18nStringReport_NoChineseStrings_ReportsZero()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "var a = \"plain\";\n");
        var result = await new I18nStringReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现中文字符串字面量", result);
    }

    [Fact]
    public async Task I18nStringReport_RejectsOutsideAndCancellation()
    {
        await Assert.ThrowsAsync<ToolException>(() => new I18nStringReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "var a = \"中\";\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new I18nStringReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
