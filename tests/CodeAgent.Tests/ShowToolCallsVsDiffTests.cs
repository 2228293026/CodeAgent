using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeAgent.Providers;
using CodeAgent.Tools;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 工具日志开关与 diff 预览的关系。
///
/// <c>showToolCalls</c> 管的是"要不要逐条念工具名"（噪音）；
/// diff 管的是"代码到底被改成了什么"（事实）。
/// 此前两者绑在同一个 <c>if (showLog)</c> 里，于是关掉工具日志的用户
/// 从头到尾**看不到一行改动内容**——对一个写代码的工具来说，那等于让它在黑箱里改文件。
/// </summary>
public sealed class ShowToolCallsVsDiffTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-stc-" + Guid.NewGuid().ToString("N"));

    public ShowToolCallsVsDiffTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static Queue<ProviderResponse> EditTurn()
    {
        var q = new Queue<ProviderResponse>();
        q.Enqueue(new ProviderResponse
        {
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "e1",
                    Name = "edit_file",
                    ArgumentsJson = """{"path":"a.txt","old_string":"old","new_string":"new"}""",
                },
            ],
        });
        q.Enqueue(new ProviderResponse { Text = "改好了" });
        return q;
    }
    [Fact]
    public async System.Threading.Tasks.Task EditStillHappensWithToolLoggingOff()
    {
        // 预览是否真的打印无法在测试里观测（直写 Console），
        // 但"关掉日志后编辑路径没被整个挡掉"是可以验证的
        var cwd = Path.Combine(Path.GetTempPath(), "codeagent-stc-w-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        File.WriteAllText(Path.Combine(cwd, "a.txt"), "old\n");
        try
        {
            var agent = new AgentClass(
                new AgentConfig { SaveSessions = false, ShowToolCalls = false },
                new FakeProvider { ResponseQueue = EditTurn() },
                ToolRegistry.CreateDefault(),
                cwd);
            await agent.RunAsync("改一下", System.Threading.CancellationToken.None);
            Assert.Equal(1, agent.TurnToolCalls);
        }
        finally { try { Directory.Delete(cwd, true); } catch { } }
    }
    [Fact]
    public void DiffPreviewIsNotInsideTheShowLogBranch()
    {
        // 纯结构性断言：预览调用必须与 showLog 判断**并列**，而不是嵌在里面。
        // 嵌进去 = 关掉工具日志就再也看不到改了��么。
        var src = File.ReadAllText(Find());
        var at = src.IndexOf("var showLog = _ctx.Config.ShowToolCalls;", StringComparison.Ordinal);
        Assert.True(at > 0, "找不到 showLog 判定");
        var window = src.Substring(at, Math.Min(2000, src.Length - at));
        var logIf = window.IndexOf("if (showLog)", StringComparison.Ordinal);
        var previewIf = window.IndexOf("tc.Name is \"edit_file\" or \"write_file\"", StringComparison.Ordinal);
        Assert.True(logIf > 0 && previewIf > 0);
        // 预览的 if 必须在 showLog 那个 if 的**闭合之后**：否则它仍在块内
        var logBlockEnd = window.IndexOf("\n        }\n", logIf, StringComparison.Ordinal);
        Assert.True(logBlockEnd > 0 && previewIf > logBlockEnd,
            "diff 预览仍嵌在 if (showLog) 里——关掉工具日志就看不到改动内容");
    }

    [Fact]
    public void DiffPreviewStillTakesTheConsoleLock()
    {
        // 移到块外之后仍要持锁：并行工具调用时预览行会与别的工具日志交错
        var src = File.ReadAllText(Find());
        var at = src.IndexOf("tc.Name is \"edit_file\" or \"write_file\"", StringComparison.Ordinal);
        var window = src.Substring(at, Math.Min(400, src.Length - at));
        Assert.Contains("lock (ConsoleLock)", window);
    }

    [Fact]
    public void ToolStartLineIsStillGatedByShowLog()
    {
        // 反向：这次改的是"预览不受限"，不是"工具名也不受限"——
        // 那正是 showToolCalls 这个开关存在的意义
        var src = File.ReadAllText(Find());
        var at = src.IndexOf("var startLine = ", StringComparison.Ordinal);
        Assert.True(at > 0);
        var before = src.Substring(Math.Max(0, at - 900), Math.Min(900, src.Length));
        Assert.Contains("if (showLog)", before);
    }

    [Fact]
    public void BothToolLoggingSettingsBehaveAsBefore()
    {
        // 前提没被破坏：默认仍是开、显式关仍是关
        Assert.True(new AgentConfig().ShowToolCalls);
        Assert.False(new AgentConfig { ShowToolCalls = false }.ShowToolCalls);
    }

    private static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = Path.Combine(dir, "src", "CodeAgent", "Agent", "Agent.cs");
                if (File.Exists(f) && new FileInfo(f).Length > 1000)
                    return f;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Agent/Agent.cs");
    }
}
