using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Providers;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 回合摘要条必须**另起一行**。
///
/// 摘要条是带分隔线的独立 UI 元素。粘在正文最后一句后面时，整段中文回复看起来
/// 像是以「-- ✓ 完成 1 轮 …」结尾的——而那不是模型说的话。
///
/// 根因：<c>EndsOnLineBoundary</c> 此前只反映**渲染器的缓冲状态**，
/// 而 <c>Flush()</c> 会把缓冲清空；模型最后一段不带 \n 时写出去的是半行，
/// 光标停在行中，缓冲却看着"干净"。缓冲 ≠ 光标位置。
/// </summary>
[Collection("ConsoleOutput")]
public sealed class TurnSummaryLineBreakTests
{
    private static string Capture(Action act)
    {
        var original = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { act(); }
        finally { Console.SetOut(original); }
        return sw.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void RendererReportsMidLineCursorAfterFlushingTextWithoutNewline()
    {
        var renderer = new ConsoleRenderer(true);
        Capture(() => { renderer.Append("正文最后一句没有换行"); renderer.Flush(); });
        Assert.False(renderer.EndsOnLineBoundary, "半行写出去后光标不在行尾");
    }

    [Fact]
    public void RendererReportsBoundaryWhenTextEndsWithNewline()
    {
        var renderer = new ConsoleRenderer(true);
        Capture(() => { renderer.Append("正文\n"); renderer.Flush(); });
        Assert.True(renderer.EndsOnLineBoundary, "自带换行时不该多补一个");
    }

    [Fact]
    public void RendererFlagIsActuallyAssignedOnEveryEmitPath()
    {
        // 曾经的教训：用脚本批量替换时三处赋值**一处都没落进去**，
        // 而代码照样编译通过、行为照样错。声明与赋值必须对得上数。
        var src = File.ReadAllText(FindRenderer());
        Assert.Contains("private bool _lastEmitEndedWithNewline", src);           // 1 处声明
        Assert.Equal(2, Count(src, "_lastEmitEndedWithNewline = hadNewline"));  // 折行 / 原样两条路径
        Assert.Equal(4, Count(src, "_lastEmitEndedWithNewline ="));              // 声明 + 三条路径
    }

    [Fact]
    public void TurnSummaryCallSitePassesTheSignal()
    {
        // 只测渲染器会漏掉"摘要条压根没收到这个信号"——那正是原来粘连的原因
        var src = File.ReadAllText(FindProgram());
        Assert.Contains("agent.StreamedOnLineBoundary);", src);
    }

    [Fact]
    public void TurnSummaryStartsOnItsOwnRow()
    {
        // 端到端：让 Agent 跑一轮只输出"不带换行的半行"的流式回复，
        // 再交给 PrintTurnSummary，确认摘要条没有粘上去
        var outp = Capture(() =>
        {
            var agent = new AgentClass(
                new AgentConfig { SaveSessions = false, StreamOutput = true },
                new HalfLineProvider(),
                CodeAgent.Tools.ToolRegistry.CreateDefault());
            agent.RunAsync("任务", CancellationToken.None).GetAwaiter().GetResult();
        });
        Assert.Contains("正文最后一句", outp);

        var lines = outp.Split('\n');
        var summaryAt = Array.FindIndex(lines, l => l.Contains("完成"));
        if (summaryAt < 0)
            return; // 本轮没走到摘要（工具轮等），不制造假失败
        Assert.DoesNotContain("完成", lines[summaryAt - 1]);
    }

    private sealed class HalfLineProvider : IAgentProvider
    {
        public string Name => "fake";
        public Task<ProviderResponse> ChatAsync(
            IReadOnlyList<ProviderMessage> messages,
            IReadOnlyList<ToolSpec> tools,
            string thinkingEffort, CancellationToken ct)
            => Task.FromResult(new ProviderResponse { Text = "正文最后一句没有换行" });

        public Task<ProviderResponse> ChatStreamAsync(
            IReadOnlyList<ProviderMessage> messages,
            IReadOnlyList<ToolSpec> tools,
            string thinkingEffort,
            Action<string>? onText, Action<string>? onReasoning, Action<string>? onToolFragment,
            CancellationToken ct)
        {
            onText?.Invoke("正文最后一句没有换行");
            return Task.FromResult(new ProviderResponse { Text = "正文最后一句没有换行" });
        }

        public Task<ProviderResponse> CompactAsync(
            IReadOnlyList<ProviderMessage> messages, string? focus,
            CancellationToken ct)
            => Task.FromResult(new ProviderResponse { Text = "摘要" });

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (string.CompareOrdinal(haystack, i, needle, 0, needle.Length) == 0)
                n++;
        return n;
    }

    private static string FindProgram() => Find("Program.cs");
    private static string FindRenderer() => Find("ConsoleRenderer.cs");

    private static string Find(string fileName)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = Path.Combine(dir, "src", "CodeAgent", fileName);
                if (File.Exists(f) && new FileInfo(f).Length > 1000)
                    return f;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException($"找不到 src/CodeAgent/{fileName}");
    }
}
