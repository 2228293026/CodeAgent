using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 配置键名与「已知键表」必须同步。
///
/// 这条课是交过学费的：新增 <c>vimMode</c> 属性后忘了登记到 <c>KnownTopLevelKeys</c>，
/// 于是 <c>Save</c> 把 <c>vimMode</c> 写进配置文件，下次启动却报
/// 「未知配置项 'vimMode'（可能是拼写错误）」。工具自己写的配置，工具自己不认识。
///
/// 所以这里不逐个点名，而是**存盘再校验**：新增任何属性而漏登记，这条立刻红。
/// </summary>
public class ConfigKnownKeysTests : IDisposable
{
    private readonly string _dir;

    public ConfigKnownKeysTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ca_cfgkeys_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    [Fact]
    public void DefaultConfigRoundTripsWithoutUnknownKeyWarnings()
    {
        var path = Path.Combine(_dir, "codeagent.json");
        AgentConfig.Save(new AgentConfig(), path);

        var warnings = AgentConfig.ValidateUnknownKeys(File.ReadAllText(path));

        Assert.Empty(warnings);
    }

    [Fact]
    public void VimModeKeyIsKnown()
    {
        // /vim 会把它写进配置；不登记的话，重启后先吃一条自造的「未知配置项」警告。
        Assert.Empty(AgentConfig.ValidateUnknownKeys("{\"vimMode\":true}"));
    }

    [Fact]
    public void HooksKeyAndItsFieldsAreKnown()
    {
        const string json = """
            {
              "hooks": [
                { "event": "preToolUse", "matcher": "*", "command": "exit 0", "timeoutSeconds": 5 }
              ]
            }
            """;

        Assert.Empty(AgentConfig.ValidateUnknownKeys(json));
    }

    [Fact]
    public void UnknownHookFieldIsReported()
    {
        const string json = """
            { "hooks": [ { "event": "preToolUse", "comand": "typo" } ] }
            """;

        var warnings = AgentConfig.ValidateUnknownKeys(json);

        Assert.Contains(warnings, w => w.Contains("hooks[0]") && w.Contains("comand"));
    }

    [Fact]
    public void InvalidHookEventIsWarnedInsteadOfSilentlyIgnored()
    {
        var path = Path.Combine(_dir, "codeagent.json");
        File.WriteAllText(path, """
            { "hooks": [ { "event": "preTool", "command": "exit 2" } ] }
            """);

        var config = AgentConfig.Load(path);

        Assert.Contains(config.Warnings, w => w.Contains("hooks[0]") && w.Contains("不会执行"));
    }

    [Fact]
    public void EmptyHookCommandIsWarned()
    {
        var path = Path.Combine(_dir, "codeagent.json");
        File.WriteAllText(path, """
            { "hooks": [ { "event": "preToolUse", "command": "   " } ] }
            """);

        var config = AgentConfig.Load(path);

        Assert.Contains(config.Warnings, w => w.Contains("hooks[0]") && w.Contains("缺少 command"));
    }
}
