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
    public void GetStringList_NullAndNonStringItems_AreSkipped()
    {
        var arr = new JsonArray();
        arr.Add("keep");
        arr.Add(null);
        arr.Add(123);
        var args = new JsonObject { ["list"] = arr };
        var list = ToolArgs.GetStringList(args, "list");
        Assert.NotNull(list);
        Assert.Single(list);
        Assert.Equal("keep", list[0]);
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
            new[] { "bash", "edit_file", "glob", "grep", "list_directory", "powershell", "read_file", "run_command", "session_search", "stop", "write_file" },
            names);
    }
}
