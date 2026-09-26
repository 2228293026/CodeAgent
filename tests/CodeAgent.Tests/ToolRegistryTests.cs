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
    public void CreateDefault_RegistersOneHundredEightyTools()
    {
        // 回归：默认工具集应完整；缺工具会让 Agent 某些能力静默失效
        var registry = ToolRegistry.CreateDefault();
        var specs = registry.ToToolSpecs();
        var names = specs.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var expected = new[]
        {
            "read_file", "read_files", "compare_files", "compare_directories", "project_stats", "code_metrics_report", "code_style_report", "comment_quality_report", "config_hardening_report", "source_composition_report", "directory_size_report", "file_info", "file_infos", "replace_in_files", "copy_file", "copy_files", "move_file", "move_files", "delete_file", "delete_files", "find_duplicates", "find_large_files", "find_stale_files", "find_unreadable_files", "find_temporary_files", "backup_file", "restore_backup", "find_empty_files", "find_recent_files", "find_filename_conflicts", "find_long_paths", "find_empty_directories", "find_symlinks", "checksum_manifest", "encoding_report", "normalize_line_endings", "create_directory", "remove_empty_directory", "git_status", "git_check_ignore", "git_check_attr", "git_ls_files", "git_repository_info", "git_repo_size_report", "git_fsck", "git_submodule_status", "git_describe", "git_contributors", "git_file_history", "git_file_stats", "git_object_info", "git_tree_entries", "git_commit_info", "git_compare_refs", "git_tag_info", "file_extension_report", "git_file_type_report", "line_ending_report", "directory_depth_report", "directory_size_breakdown", "project_manifest_report", "target_framework_report", "config_key_report", "config_key_usage_report", "readme_section_report", "csharp_keyword_report", "todo_comment_report", "record_mutability_report", "changelog_report", "i18n_string_report", "inheritance_shape_report", "magic_number_report", "large_method_report", "readme_link_check", "doc_comment_report", "doc_freshness_report", "http_url_audit_report", "test_inventory_report", "test_flakiness_report", "test_assertion_report", "debug_leftover_report", "exception_handling_report", "resource_leak_report", "naming_convention_report", "todo_marker_report", "ci_workflow_report", "workflow_hygiene_report", "license_inventory_report", "dependency_lockfile_report", "nuget_reference_report", "json_schema_report", "secret_scan_report", "env_var_reference_report", "sensitive_file_inventory", "gitignore_pattern_report", "gitignore_quality_report", "gitignore_rule_report", "editorconfig_report", "gitattributes_quality_report", "gitattributes_report", "git_hook_inventory", "git_executable_bit_report", "git_config_report", "git_lfs_report", "git_gpg_status_report", "git_ignored_files", "git_path_diff", "git_index_diagnostics", "git_worktree_details", "git_stash_info", "git_diff", "git_log", "git_blame", "git_show", "git_branches", "git_branch_age_report", "git_remotes", "git_remote_sync_report", "git_tags", "git_reflog", "git_worktrees", "git_stashes", "write_file", "edit_file", "list_directory",
            "glob", "grep", "run_command", "bash", "powershell",
            "session_search", "stop", "apply_patch", "async_correctness_report", "string_performance_report", "null_coalescing_report", "operator_overload_report", "null_safety_report", "partial_method_report", "logging_practice_report", "api_usage_report", "api_compatibility_report", "data_contract_report", "performance_anti_pattern_report", "test_quality_report", "resource_lifecycle_report", "localization_readiness_report", "enum_usage_report", "disposable_pattern_report", "documentation_sync_report", "input_sanitization_report", "observability_report", "build_config_report", "cache_strategy_report", "concurrency_safety_report", "collection_usage_report", "expensive_call_report", "state_management_report", "string_format_report", "log_hygiene_report", "file_safety_report", "dependency_version_report", "equality_contract_report", "error_handling_detail_report", "event_subscription_report", "extension_method_report", "collection_performance_report", "concurrency_risk_report", "dead_implementation_report", "input_validation_report", "argument_safety_report", "duplicate_code_report", "duplicate_method_report", "dead_branch_report", "lifetime_bounds_report", "numeric_literal_report", "nullable_annotation_report", "obsolete_usage_report", "test_isolation_report", "using_directive_report",
        };
        foreach (var name in expected)
            Assert.True(names.Contains(name), $"缺少工具: {name}");
        Assert.Equal(180, expected.Length); // 不应有多余工具
        Assert.Equal(expected.Length, specs.Count);
        foreach (var spec in specs)
        {
            Assert.False(string.IsNullOrWhiteSpace(spec.Description), $"{spec.Name} 缺描述");
            Assert.NotNull(spec.Parameters);
        }
    }
}
