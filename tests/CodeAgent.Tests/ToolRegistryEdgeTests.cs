using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>ToolArgs 容错与 ToolRegistry 注册/分发的边界测试(补充 ToolArgsTests / ToolRegistryTests)。</summary>
public class ToolRegistryEdgeTests
{
    private static AgentContext MakeContext() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(Path.GetTempPath()),
    };

    // ===== ToolArgs 容错 =====

    [Fact]
    public void GetString_NullValue_ReturnsDefault()
    {
        var args = new JsonObject { ["x"] = null };
        Assert.Equal("dft", ToolArgs.GetString(args, "x", "dft"));
    }

    [Fact]
    public void GetString_KeyExistsButNullObjectValue_ReturnsDefault()
    {
        // 值为 JSON null：不是 JsonValue → 默认
        var args = new JsonObject();
        args["x"] = null;
        Assert.Equal("", ToolArgs.GetString(args, "x"));
    }

    [Fact]
    public void GetInt_BoolValue_FallsBackToDefault()
    {
        var args = new JsonObject { ["n"] = true };
        Assert.Equal(7, ToolArgs.GetInt(args, "n", 7)); // 布尔既非 int 也非可解析字符串
    }

    [Fact]
    public void GetInt_EmptyString_FallsBackToDefault()
    {
        var args = new JsonObject { ["n"] = "" };
        Assert.Equal(3, ToolArgs.GetInt(args, "n", 3));
    }

    [Fact]
    public void GetBool_UnknownString_FallsBackToDefault()
    {
        var args = new JsonObject { ["b"] = "maybe" };
        Assert.True(ToolArgs.GetBool(args, "b", true)); // 未知字符串 → 默认
    }

    [Fact]
    public void GetBool_UpperCaseAndAbbrev_AreTrue()
    {
        Assert.True(ToolArgs.GetBool(new JsonObject { ["b"] = "TRUE" }, "b", false));
        Assert.True(ToolArgs.GetBool(new JsonObject { ["b"] = "Yes" }, "b", false));
        Assert.True(ToolArgs.GetBool(new JsonObject { ["b"] = "Y" }, "b", false));
        Assert.True(ToolArgs.GetBool(new JsonObject { ["b"] = "1" }, "b", false));
    }

    [Fact]
    public void GetBool_NumericValue_IsCoerced()
    {
        // 回归：模型常把布尔参数误发成整数（1/0）——数字 1/非0 视为 true，0 视为 false
        Assert.True(ToolArgs.GetBool(new JsonObject { ["b"] = 1 }, "b", false));
        Assert.True(ToolArgs.GetBool(new JsonObject { ["b"] = 5 }, "b", false));
        Assert.False(ToolArgs.GetBool(new JsonObject { ["b"] = 0 }, "b", true));
    }

    [Fact]
    public void GetStringDict_NullValue_IsSkipped()
    {
        var args = new JsonObject
        {
            ["env"] = new JsonObject { ["A"] = "1", ["B"] = null },
        };
        var dict = ToolArgs.GetStringDict(args, "env");
        Assert.NotNull(dict);
        Assert.Single(dict);
        Assert.Equal("1", dict["A"]);
    }

    [Fact]
    public void GetStringList_NullSkipped_IntCoercedToString()
    {
        // null 项跳过；数字项按字符串处理（模型偶发把数组项发成数字，如 include:["*.cs", 5]）
        var arr = new JsonArray();
        arr.Add("keep");
        arr.Add(null);
        arr.Add(123);
        var args = new JsonObject { ["list"] = arr };
        var list = ToolArgs.GetStringList(args, "list");
        Assert.NotNull(list);
        Assert.Equal(2, list!.Count);
        Assert.Equal("keep", list[0]);
        Assert.Equal("123", list[1]);
    }

    [Fact]
    public void GetStringList_SingleIntValue_CoercedToString()
    {
        var args = new JsonObject { ["list"] = 7 };
        var list = ToolArgs.GetStringList(args, "list");
        Assert.NotNull(list);
        Assert.Equal("7", list![0]);
    }

    [Fact]
    public void GetStringSet_StringArray_ReturnsCaseInsensitiveSet()
    {
        var args = new JsonObject { ["set"] = new JsonArray("A", "b", "A") };
        var set = ToolArgs.GetStringSet(args, "set");
        Assert.NotNull(set);
        Assert.Equal(2, set!.Count);
        Assert.Contains("a", set);
        Assert.Contains("B", set);
    }

    [Fact]
    public void GetStringSet_MissingKey_ReturnsNull()
    {
        var set = ToolArgs.GetStringSet(new JsonObject(), "missing");
        Assert.Null(set);
    }

    // ===== ToolRegistry 注册与分发 =====

    private sealed class FakeTool(string name, string result = "ok") : ITool
    {
        public string Name { get; } = name;
        public string Description => "fake";
        public JsonObject Parameters { get; } = new();
        public Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct) =>
            Task.FromResult($"{result}:{args?["echo"]}");
    }

    [Fact]
    public async Task Register_OverwritesByName()
    {
        var reg = new ToolRegistry();
        reg.Register(new FakeTool("t"));
        reg.Register(new FakeTool("t", "second"));
        Assert.Single(reg.ToToolSpecs());
        // 注册覆盖生效：执行返回第二个工具的结果而非第一个
        var result = await reg.ExecuteAsync("t", "{}", MakeContext(), CancellationToken.None);
        Assert.StartsWith("second:", result);
    }

    [Fact]
    public void ToToolSpecs_ReflectsRegisteredTools()
    {
        var reg = new ToolRegistry();
        reg.Register(new FakeTool("alpha"));
        reg.Register(new FakeTool("beta"));
        var specs = reg.ToToolSpecs().Select(s => s.Name).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "alpha", "beta" }, specs);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownTool_Throws()
    {
        var reg = new ToolRegistry();
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => reg.ExecuteAsync("nope", "{}", MakeContext(), CancellationToken.None));
        Assert.Contains("未知工具", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_BlankArgs_TreatedAsEmptyObject()
    {
        var reg = new ToolRegistry();
        reg.Register(new FakeTool("t", "done"));
        var result = await reg.ExecuteAsync("t", "   ", MakeContext(), CancellationToken.None);
        Assert.StartsWith("done:", result); // 空白参数不抛非法 JSON
    }

    [Fact]
    public async Task ExecuteAsync_InvalidJson_Throws()
    {
        var reg = new ToolRegistry();
        reg.Register(new FakeTool("t"));
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => reg.ExecuteAsync("t", "{ not json", MakeContext(), CancellationToken.None));
        Assert.Contains("不是合法 JSON", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_NonObjectJson_TreatedAsEmpty()
    {
        var reg = new ToolRegistry();
        reg.Register(new FakeTool("t", "done"));
        var result = await reg.ExecuteAsync("t", "[1,2]", MakeContext(), CancellationToken.None);
        Assert.StartsWith("done:", result); // 数组标量 → 空对象兜底
    }

    [Fact]
    public async Task ExecuteAsync_DispatchesArgsToTool()
    {
        var reg = new ToolRegistry();
        reg.Register(new FakeTool("t", "echoed"));
        var result = await reg.ExecuteAsync("t", """{"echo":"hello"}""", MakeContext(), CancellationToken.None);
        Assert.Equal("echoed:hello", result);
    }

    [Fact]
    public void CreateDefault_RegistersExpectedToolSet()
    {
        var reg = ToolRegistry.CreateDefault();
        var names = reg.ToToolSpecs().Select(s => s.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(
            new[] { "api_compatibility_report", "api_usage_report", "apply_patch", "argument_safety_report", "async_correctness_report", "backup_file", "bash", "boolean_parameter_report", "boxing_allocation_report", "build_config_report", "cache_strategy_report", "changelog_report", "checksum_manifest", "ci_workflow_report", "code_metrics_report", "code_style_report", "collection_performance_report", "collection_usage_report", "comment_quality_report", "compare_directories", "compare_files", "concurrency_risk_report", "concurrency_safety_report", "concurrent_mutation_report", "config_hardening_report", "config_key_report", "config_key_usage_report", "copy_file", "copy_files", "create_directory", "csharp_keyword_report", "culture_format_report", "data_contract_report", "dead_branch_report", "dead_implementation_report", "debug_leftover_report", "deferred_query_report", "delete_file", "delete_files", "dependency_inversion_report", "dependency_lockfile_report", "dependency_version_report", "directory_depth_report", "directory_size_breakdown", "directory_size_report", "disposable_pattern_report", "doc_comment_report", "doc_freshness_report", "documentation_sync_report", "duplicate_code_report", "duplicate_logic_report", "duplicate_method_report", "edit_file", "editorconfig_report", "encoding_report", "enum_usage_report", "env_var_reference_report", "equality_contract_report", "equals_gethashcode_report", "error_handling_detail_report", "event_subscription_report", "exception_handling_report", "expensive_call_report", "extension_method_report", "file_extension_report", "file_info", "file_infos", "file_safety_report", "find_duplicates", "find_empty_directories", "find_empty_files", "find_filename_conflicts", "find_large_files", "find_long_paths", "find_recent_files", "find_stale_files", "find_symlinks", "find_temporary_files", "find_unreadable_files", "git_blame", "git_branch_age_report", "git_branches", "git_check_attr", "git_check_ignore", "git_commit_info", "git_compare_refs", "git_config_report", "git_contributors", "git_describe", "git_diff", "git_executable_bit_report", "git_file_history", "git_file_stats", "git_file_type_report", "git_fsck", "git_gpg_status_report", "git_hook_inventory", "git_ignored_files", "git_index_diagnostics", "git_lfs_report", "git_log", "git_ls_files", "git_object_info", "git_path_diff", "git_reflog", "git_remote_sync_report", "git_remotes", "git_repo_size_report", "git_repository_info", "git_show", "git_stash_info", "git_stashes", "git_status", "git_submodule_status", "git_tag_info", "git_tags", "git_tree_entries", "git_worktree_details", "git_worktrees", "gitattributes_quality_report", "gitattributes_report", "gitignore_pattern_report", "gitignore_quality_report", "gitignore_rule_report", "glob", "grep", "hardcoded_secret_report", "http_url_audit_report", "i18n_string_report", "inheritance_shape_report", "input_sanitization_report", "input_validation_report", "interface_contract_report", "json_schema_report", "large_method_report", "license_inventory_report", "lifetime_bounds_report", "line_ending_mixed_report", "line_ending_report", "linq_query_report", "list_directory", "localization_readiness_report", "lock_misuse_report", "log_hygiene_report", "logging_practice_report", "magic_number_report", "move_file", "move_files", "naming_convention_report", "normalize_line_endings", "nuget_reference_report", "null_assignment_report", "null_coalescing_report", "null_safety_report", "nullable_annotation_report", "numeric_literal_report", "observability_report", "obsolete_usage_report", "operator_overload_report", "optional_param_dropped_report", "partial_method_report", "path_escape_report", "performance_anti_pattern_report", "powershell", "project_manifest_report", "project_stats", "public_api_surface_report", "read_file", "read_files", "readme_link_check", "readme_section_report", "record_mutability_report", "remove_empty_directory", "replace_in_files", "resource_leak_report", "resource_lifecycle_report", "restore_backup", "run_command", "secret_scan_report", "sensitive_file_inventory", "session_search", "silent_catch_report", "source_composition_report", "stale_comment_report", "state_management_report", "stop", "string_concat_perf_report", "string_format_report", "string_performance_report", "swallowed_exception_report", "switch_exhaustiveness_report", "target_framework_report", "test_assertion_report", "test_flakiness_report", "test_inventory_report", "test_isolation_report", "test_placeholder_report", "test_quality_report", "testability_report", "time_unit_confusion_report", "todo_comment_report", "todo_marker_report", "undisposed_stream_report", "using_directive_report", "workflow_hygiene_report", "write_file" },
            names);
    }
}
