using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class ModesTests
{
    [Fact]
    public void All_ContainsEightBuiltinModes()
    {
        Assert.Equal(8, Modes.All.Length);
        Assert.Contains(Modes.All, m => m.Name == "code");
        Assert.Contains(Modes.All, m => m.Name == "plan");
        Assert.Contains(Modes.All, m => m.Name == "review");
    }

    [Fact]
    public void ReadOnlyModes_RestrictTools()
    {
        foreach (var name in new[] { "plan", "explain", "review" })
        {
            var mode = Modes.All.First(m => m.Name == name);
            Assert.NotNull(mode.AllowedTools);
            Assert.DoesNotContain(mode.AllowedTools!, t => t == "write_file" || t == "edit_file" || t == "run_command");
            Assert.Contains("read_file", mode.AllowedTools!);
        }
    }

    [Fact]
    public void Build_IncludesCustomModes()
    {
        var config = new AgentConfig
        {
            Modes =
            {
                new AgentModeConfig { Name = "fix", Description = "修复模式", SystemPrompt = "fix prompt", Tools = ["read_file", "edit_file"] },
                new AgentModeConfig { Name = "", Description = "无名模式应被跳过" },
            },
        };

        var modes = Modes.Build(config);
        Assert.Equal(Modes.All.Length + 1, modes.Count); // 空白名自定义模式被跳过
        var fix = modes.First(m => m.Name == "fix");
        Assert.Equal("修复模式", fix.Description);
        Assert.Equal("fix prompt", fix.SystemPrompt);
        Assert.Equal(new[] { "read_file", "edit_file" }, fix.AllowedTools);
    }

    [Fact]
    public void Build_CustomModeWithoutTools_AllowsAll()
    {
        var config = new AgentConfig
        {
            Modes = { new AgentModeConfig { Name = "free", Description = "全功能" } },
        };

        var mode = Modes.Build(config).First(m => m.Name == "free");
        Assert.Null(mode.AllowedTools); // 未指定 tools = 全部工具
    }

    [Fact]
    public void Find_CaseInsensitiveAndFallback()
    {
        var config = new AgentConfig();
        Assert.Equal("code", Modes.Find("CODE", config).Name); // 大小写不敏感
        Assert.Equal("plan", Modes.Find("plan", config).Name);
        Assert.Equal("code", Modes.Find("no-such-mode", config).Name); // 未匹配回退 code
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Find_NullOrEmptyName_FallsBackToCode(string? name)
    {
        // 回归：config.defaultMode 为 null（JSON 显式 "defaultMode": null）时
        // name.Trim() 曾抛 NullReferenceException 导致 codeagent 启动崩溃
        var config = new AgentConfig();
        Assert.Equal("code", Modes.Find(name!, config).Name);
    }

    [Fact]
    public void Find_CustomMode_ResolvesByName()
    {
        var config = new AgentConfig
        {
            Modes = { new AgentModeConfig { Name = "audit", Description = "审计" } },
        };
        Assert.Equal("audit", Modes.Find("audit", config).Name);
        Assert.Equal("audit", Modes.Find("AUDIT", config).Name); // 大小写不敏感
    }

    [Fact]
    public void ListText_ContainsAllModeNames()
    {
        var config = new AgentConfig
        {
            Modes = { new AgentModeConfig { Name = "fix", Description = "修复模式" } },
        };
        var text = Modes.ListText(config);
        Assert.Contains("code", text);
        Assert.Contains("plan", text);
        Assert.Contains("fix", text); // 自定义模式也在列表
        Assert.Contains("— 修复模式", text);
    }

    [Fact]
    public void Build_CustomModeWithoutDescription_UsesNameAsFallback()
    {
        var config = new AgentConfig
        {
            Modes = { new AgentModeConfig { Name = "bare" } },
        };
        var mode = Modes.Build(config).First(m => m.Name == "bare");
        Assert.Equal("bare", mode.Description); // 空描述回退为名称
        Assert.Equal(AgentConfig.DefaultSystemPrompt, mode.SystemPrompt); // 空提示回退默认
    }

    [Fact]
    public void Build_ReadOnlyModes_AllRestrictWrites()
    {
        // 所有只读模式（plan/explain/review）都不应暴露写工具
        foreach (var name in new[] { "plan", "explain", "review" })
        {
            var mode = Modes.All.First(m => m.Name == name);
            foreach (var t in mode.AllowedTools!)
                Assert.DoesNotContain(t, new[] { "write_file", "edit_file", "run_command", "bash", "powershell" });
        }
    }

    [Fact]
    public void All_EveryModeHasSystemPrompt()
    {
        foreach (var m in Modes.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.SystemPrompt));
            Assert.False(string.IsNullOrWhiteSpace(m.Description));
        }
    }

    [Fact]
    public void All_CodeModeIsDefault_AllowsAllTools()
    {
        var code = Modes.All[0];
        Assert.Equal("code", code.Name);
        Assert.Null(code.AllowedTools); // code 模式不限制工具
    }

    [Fact]
    public void Build_CustomModeWithEmptyToolsArray_AllowsAll()
    {
        // 文档约定：modes[].tools 空数组 = 全部工具（与缺省 tools 等价）
        // README「自定义模式」与 codeagent.example.json 的 fix 模式都用 "tools": []
        var config = new AgentConfig
        {
            Modes = { new AgentModeConfig { Name = "fix", Description = "修复模式", SystemPrompt = "fix prompt", Tools = [] } },
        };

        var mode = Modes.Build(config).First(m => m.Name == "fix");
        Assert.Null(mode.AllowedTools); // 空数组 = 全部工具
    }
}
