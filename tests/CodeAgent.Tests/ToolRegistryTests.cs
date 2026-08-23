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
    public void CreateDefault_RegistersAllElevenTools()
    {
        // 回归：默认工具集应完整；缺工具会让 Agent 某些能力静默失效
        var registry = ToolRegistry.CreateDefault();
        var specs = registry.ToToolSpecs();
        var names = specs.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var expected = new[]
        {
            "read_file", "write_file", "edit_file", "list_directory",
            "glob", "grep", "run_command", "bash", "powershell",
            "session_search", "stop",
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
