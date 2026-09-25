using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public class ToolRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-registry-" + Guid.NewGuid().ToString("N"));

    public ToolRegistryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    private static AgentContext MakeContext(string dir) => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(dir),
    };

    [Fact]
    public async Task ExecuteAsync_CanceledToken_StopsBeforeDispatch()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ToolRegistry.CreateDefault().ExecuteAsync(
                "read_file", "{}", MakeContext(_dir), cts.Token));
    }

    [Fact]
    public async Task StopTool_CanceledToken_DoesNotRequestStop()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ctx = MakeContext(_dir);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new StopTool().ExecuteAsync(new JsonObject { ["reason"] = "done" }, ctx, cts.Token));
        Assert.False(ctx.StopRequested);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownTool_ThrowsWithAvailableList()
    {
        var registry = ToolRegistry.CreateDefault();
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            registry.ExecuteAsync("no_such_tool", "{}", MakeContext(_dir), CancellationToken.None));
        Assert.Contains("未知工具", ex.Message);
        Assert.Contains("read_file", ex.Message); // 错误信息应列出可用工具
    }

    [Fact]
    public async Task ExecuteAsync_InvalidJsonArgs_Throws()
    {
        var registry = ToolRegistry.CreateDefault();
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            registry.ExecuteAsync("read_file", "{not json", MakeContext(_dir), CancellationToken.None));
        Assert.Contains("不是合法 JSON", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ReadFileWithMissingPath_ThrowsHelpful()
    {
        var registry = ToolRegistry.CreateDefault();
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            registry.ExecuteAsync("read_file", "{}", MakeContext(_dir), CancellationToken.None));
        Assert.Contains("path", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyArgsJson_IsTolerated()
    {
        // 工具参数为空字符串或缺失时按空对象处理，不抛解析错误
        var registry = ToolRegistry.CreateDefault();
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            registry.ExecuteAsync("read_file", "", MakeContext(_dir), CancellationToken.None));
        Assert.Contains("path", ex.Message); // 走到缺参校验而非 JSON 解析错误
    }

    [Fact]
    public void CreateDefault_RegistersAllSeventyOneTools()
    {
        // 回归：默认工具集应完整；缺工具会让 Agent 某些能力静默失效
        var registry = ToolRegistry.CreateDefault();
        var specs = registry.ToToolSpecs();
        var names = specs.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var expected = new[]
        {
            "read_file", "read_files", "compare_files", "compare_directories", "project_stats", "directory_size_report", "file_info", "file_infos", "replace_in_files", "copy_file", "copy_files", "move_file", "move_files", "delete_file", "delete_files", "find_duplicates", "find_large_files", "find_stale_files", "find_unreadable_files", "find_temporary_files", "backup_file", "restore_backup", "find_empty_files", "find_recent_files", "find_filename_conflicts", "find_long_paths", "find_empty_directories", "find_symlinks", "checksum_manifest", "encoding_report", "normalize_line_endings", "create_directory", "remove_empty_directory", "git_status", "git_check_ignore", "git_check_attr", "git_ls_files", "git_repository_info", "git_fsck", "git_submodule_status", "git_describe", "git_contributors", "git_file_history", "git_file_stats", "git_object_info", "git_tree_entries", "git_commit_info", "git_compare_refs", "git_tag_info", "file_extension_report", "git_diff", "git_log", "git_blame", "git_show", "git_branches", "git_remotes", "git_tags", "git_reflog", "git_worktrees", "git_stashes", "write_file", "edit_file", "list_directory",
            "glob", "grep", "run_command", "bash", "powershell",
            "session_search", "stop", "apply_patch",
        };
        foreach (var name in expected)
            Assert.True(names.Contains(name), $"缺少工具: {name}");
        Assert.Equal(expected.Length, specs.Count); // 不应有多余工具
        foreach (var spec in specs)
        {
            Assert.False(string.IsNullOrWhiteSpace(spec.Description), $"{spec.Name} 缺描述");
            Assert.NotNull(spec.Parameters);
        }
    }
}
