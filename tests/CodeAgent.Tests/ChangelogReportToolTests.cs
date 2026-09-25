using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ChangelogReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-changelog-" + Guid.NewGuid().ToString("N"));

    public ChangelogReportToolTests() => Directory.CreateDirectory(_dir);

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
    public void Parse_SplitsReleasesAndCountsCategories()
    {
        var releases = ChangelogReportTool.Parse(new[]
        {
            "# Changelog",
            "",
            "## [1.2.0] - 2024-05-01",
            "### Added",
            "- 新增工具",
            "- 又一个",
            "### Fixed",
            "- 修复 x",
            "",
            "## 1.1.0",
            "- Changed: 改名",
        });
        Assert.Equal(2, releases.Count);
        Assert.Equal("1.2.0", releases[0].Version);
        Assert.Equal("2024-05-01", releases[0].Date);
        Assert.Equal(2, releases[0].Counts["新增"]);
        Assert.Equal(1, releases[0].Counts["修复"]);
        Assert.Equal("1.1.0", releases[1].Version);
        Assert.Null(releases[1].Date);
        Assert.Equal(1, releases[1].Counts["变更"]);
    }

    [Fact]
    public void Parse_SkipsHeadingsInsideCodeFences()
    {
        var releases = ChangelogReportTool.Parse(new[]
        {
            "## 1.0.0",
            "```",
            "## 9.9.9",
            "```",
            "- Added: x",
        });
        Assert.Single(releases);
        Assert.Equal("1.0.0", releases[0].Version);
        Assert.Equal(1, releases[0].Counts["新增"]);
    }

    [Fact]
    public void Parse_NoReleases_ReturnsEmpty()
    {
        Assert.Empty(ChangelogReportTool.Parse(new[] { "# Changelog", "just text" }));
    }

    [Fact]
    public void Parse_SubsectionHeadingSuppliesCategoryForPlainBullets()
    {
        // Keep-a-Changelog 风格：### Added 下是无前缀条目，不应全部漏统计
        var releases = ChangelogReportTool.Parse(new[]
        {
            "## 1.0.0",
            "### Security",
            "- 修掉一个漏洞",
            "- 再修一个",
            "### Added",
            "- 全新工具",
        });
        Assert.Single(releases);
        Assert.Equal(2, releases[0].Counts["安全"]);
        Assert.Equal(1, releases[0].Counts["新增"]);
    }

    [Fact]
    public async Task ChangelogReport_RendersReleaseSummary()
    {
        File.WriteAllText(Path.Combine(_dir, "CHANGELOG.md"), string.Join("\n",
            "# Changelog", "", "## 2.0.0 - 2025-01-15", "- Added: a", "- Fixed: b", "## 1.0.0 - 2024-01-01", "- Removed: c"));
        var result = await new ChangelogReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("CHANGELOG 报告: 2 个版本，3 条条目", result);
        Assert.Contains("2.0.0 · 2025-01-15 · 新增 1 / 修复 1", result);
        Assert.Contains("1.0.0 · 2024-01-01 · 移除 1", result);
    }

    [Fact]
    public async Task ChangelogReport_RejectsMissingFileOutsideAndCancellation()
    {
        File.WriteAllText(Path.Combine(_dir, "notes.md"), "# hi");
        await Assert.ThrowsAsync<ToolException>(() => new ChangelogReportTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        File.WriteAllText(Path.Combine(_dir, "CHANGELOG.md"), "## 1.0.0");
        await Assert.ThrowsAsync<ToolException>(() => new ChangelogReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ChangelogReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
