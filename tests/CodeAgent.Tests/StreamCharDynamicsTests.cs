using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent;
using CodeAgent.Providers;
using CodeAgent.Tools;
using Xunit;
using AgentCore = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>逐字吐字的假 Provider：模拟真实模型的流式增量。</summary>
public sealed class CharByCharProvider : IAgentProvider
{
    public string Name => "char-by-char";
    public required string Text { get; init; }

    public Task<ProviderResponse> ChatAsync(
        IReadOnlyList<ProviderMessage> messages, IReadOnlyList<ToolSpec> tools,
        string thinkingEffort, CancellationToken ct) =>
        Task.FromResult(new ProviderResponse { Text = Text });

    public Task<ProviderResponse> ChatStreamAsync(
        IReadOnlyList<ProviderMessage> messages, IReadOnlyList<ToolSpec> tools,
        string thinkingEffort, Action<string>? onText, Action<string>? onReasoning,
        Action<string>? onToolFragment, CancellationToken ct)
    {
        foreach (var ch in Text)
            onText?.Invoke(ch.ToString());
        return Task.FromResult(new ProviderResponse { Text = Text });
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(["char-by-char"]);
}

/// <summary>先吐一段不带换行的思考，再逐字吐正文：复现 thinkingEffort 开启时的真实顺序。</summary>
public sealed class ReasoningThenCharProvider : IAgentProvider
{
    public string Name => "reasoning-then-char";

    public Task<ProviderResponse> ChatAsync(
        IReadOnlyList<ProviderMessage> messages, IReadOnlyList<ToolSpec> tools,
        string thinkingEffort, CancellationToken ct) =>
        Task.FromResult(new ProviderResponse { Text = "逐字答案" });

    public Task<ProviderResponse> ChatStreamAsync(
        IReadOnlyList<ProviderMessage> messages, IReadOnlyList<ToolSpec> tools,
        string thinkingEffort, Action<string>? onText, Action<string>? onReasoning,
        Action<string>? onToolFragment, CancellationToken ct)
    {
        // 关键：思考不带换行（真实模型的 reasoning 增量就是这样）
        onReasoning?.Invoke("思考内容");
        foreach (var ch in "逐字答案")
            onText?.Invoke(ch.ToString());
        return Task.FromResult(new ProviderResponse { Text = "逐字答案" });
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(["reasoning-then-char"]);
}

/// <summary>
/// 逐字动态：模型每吐一个字，屏幕上就要跟着长一个字。
///
/// 回归：<see cref="ConsoleRenderer"/> 把字符攒进行缓冲，**凑齐一整行才写出去**。
/// 模型是逐字返回的，于是屏幕纹丝不动，直到整行结束才「啪」地蹦出一整行——
/// 用户看到的是「卡很久，然后一行行跳出来」。
///
/// 这组测试专门覆盖**整条链路**（Provider → Agent → renderer → 屏幕）：
/// 渲染器单帧行为已由 <see cref="LivePreviewStreamTests"/> 覆盖，但那只证明
/// 「给它增量它会画」，证明不了「Agent 真的把每个增量都交给它了」。
/// </summary>
[Collection("ConsoleOutput")]
public class StreamCharDynamicsTests
{
    private static string Capture(Func<Task> act)
    {
        var original = Console.Out;
        var sw = new StringWriter { NewLine = "\n" };
        Console.SetOut(sw);
        try { act().GetAwaiter().GetResult(); }
        finally { Console.SetOut(original); sw.Flush(); }
        return sw.ToString().Replace("\r\n", "\n");
    }

    private static AgentCore MakeAgent(IAgentProvider provider) => new(
        new AgentConfig { SaveSessions = false, StreamOutput = true, RenderMarkdown = true, TuiAnsi = true },
        provider,
        ToolRegistry.CreateDefault());

    [Fact]
    public void CharactersAppearBeforeTheLineIsComplete()
    {
        // 核心断言：换行还没到，前面的字已经在输出里了。
        // 旧实现里 `\n` 之前的每一个字都不会出现。
        var outp = Capture(() => MakeAgent(new CharByCharProvider { Text = "逐字长出来" })
            .RunAsync("说点什么", CancellationToken.None));

        var firstLine = outp.Split('\n')[0];
        Assert.Contains("逐字长出来", StripControls(firstLine), StringComparison.Ordinal);
    }

    [Fact]
    public void IntermediateFramesAreErasedNotAccumulated()
    {
        // 逐字重绘必须「擦掉上一帧再画」，否则屏幕上会是「逐逐字逐字长…」。
        // 这里按终端语义回放，断言最终画面里每个字只出现一次。
        var outp = Capture(() => MakeAgent(new CharByCharProvider { Text = "一二三四五" })
            .RunAsync("说点什么", CancellationToken.None));

        var screen = ReplayTerminal(outp);
        Assert.Contains("一二三四五", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("一二一二", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void CharactersKeepComingWhenPreviewIsActuallyOn()
    {
        // 上面两条测试在测试宿主里跑的是「预览关闭」分支（输出被重定向），
        // 只证明不了预览打开时整条链路仍然逐字。这里把两个前提**都按真终端伪造**：
        // 重定向状态走 SafeColor.IsRedirected 注入点，宽度走 COLUMNS（重定向时 Program 就从这儿读）。
        //
        // 不伪造的话，Agent 里那行接线等于没有端到端测试能碰到——
        // 断言会一直是绿的，哪怕预览根本没被启用。
        var prevRedirected = SafeColor.IsRedirected;
        var prevColumns = Environment.GetEnvironmentVariable("COLUMNS");
        SafeColor.IsRedirected = () => false;
        Environment.SetEnvironmentVariable("COLUMNS", "80");
        try
        {
            var outp = Capture(() => MakeAgent(new CharByCharProvider { Text = "慢慢长出来" })
                .RunAsync("说点什么", CancellationToken.None));

            // 预览分支的特征：输出里出现原地重绘序列
            Assert.Contains("\x1b[2K", outp, StringComparison.Ordinal);
            // 而且最终画面上内容完整、没有叠加残字
            var screen = ReplayTerminal(outp);
            Assert.Contains("慢慢长出来", screen, StringComparison.Ordinal);
        }
        finally
        {
            SafeColor.IsRedirected = prevRedirected;
            Environment.SetEnvironmentVariable("COLUMNS", prevColumns);
        }
    }

    [Fact]
    public void AnswerAfterReasoningStillStreamsCharByChar()
    {
        // 用户实际的主路径：thinkingEffort=high 时几乎每轮都先出思考、再出正文。
        // 思考文本是裸写的（不带换行），渲染器会记下「光标在行中」；
        // 若「思考→正文」那个补换行不通知渲染器，CanPreview 会一直 false——
        // 也就是说**整段答案都不会再逐字长出来**，本轮修复等于没生效。
        var prevRedirected = SafeColor.IsRedirected;
        var prevColumns = Environment.GetEnvironmentVariable("COLUMNS");
        SafeColor.IsRedirected = () => false;
        Environment.SetEnvironmentVariable("COLUMNS", "80");
        try
        {
            var outp = Capture(() => MakeAgent(new ReasoningThenCharProvider())
                .RunAsync("说点什么", CancellationToken.None));

            // 正文必须逐字重绘：输出里要出现原地重绘序列
            Assert.Contains("\x1b[2K", outp, StringComparison.Ordinal);
            // 而且思考与正文确实分开成了两行
            var plain = StripControls(outp);
            Assert.Contains("思考内容", plain, StringComparison.Ordinal);
            Assert.Contains("逐字答案", plain, StringComparison.Ordinal);
        }
        finally
        {
            SafeColor.IsRedirected = prevRedirected;
            Environment.SetEnvironmentVariable("COLUMNS", prevColumns);
        }
    }

    [Fact]
    public void NoRedrawSequencesAreEmittedWhenOutputIsPiped()
    {
        // 反向：管道里**绝不能**出现 \r + ESC[2K。控制序列混进日志是永久性污染，
        // 用户 grep 到的东西会全是 `[2K` 这样的噪声。
        var prevRedirected = SafeColor.IsRedirected;
        var prevColumns = Environment.GetEnvironmentVariable("COLUMNS");
        SafeColor.IsRedirected = () => true;
        Environment.SetEnvironmentVariable("COLUMNS", "80");
        try
        {
            var outp = Capture(() => MakeAgent(new CharByCharProvider { Text = "管道输出" })
                .RunAsync("说点什么", CancellationToken.None));

            Assert.DoesNotContain("\x1b[2K", outp, StringComparison.Ordinal);
            Assert.Contains("管道输出", StripControls(outp), StringComparison.Ordinal);
        }
        finally
        {
            SafeColor.IsRedirected = prevRedirected;
            Environment.SetEnvironmentVariable("COLUMNS", prevColumns);
        }
    }

    [Fact]
    public void PreviewIsOnlyActiveInARealTerminal()
    {
        // 重定向时预览**不该**被启用（否则控制序列会混进管道）。
        Assert.False(ConsoleRenderer.ShouldLivePreview(
            renderMarkdown: true, tuiAnsi: true, outputRedirected: true));
    }

    [Fact]
    public void PreviewNeedsMarkdownAndAnsi()
    {
        // 关掉 Markdown 渲染时 Append 直接 Console.Write，本来就逐字，不需要预览；
        // 关掉 ANSI 说明终端不吃控制序列，原地重绘会变成一屏残帧。
        Assert.False(ConsoleRenderer.ShouldLivePreview(true, false, false));
        Assert.False(ConsoleRenderer.ShouldLivePreview(false, true, false));
        Assert.True(ConsoleRenderer.ShouldLivePreview(true, true, false));
    }

    [Fact]
    public void StatsLineIsNotGluedToTheEndOfReasoning()
    {
        // 真机症状（用户贴出来的输出）：
        //   我选择直接简洁回复。✓ 用时 11s · ↑ 163 tokens
        // 统计行接在思考文本的行尾，读起来像思考的最后一句话。
        //
        // 根因是顺序：FinalizeSpinner 排在「思考↔正文断行」之前。
        // 思考是裸写的、末尾不带换行，于是统计行的 WriteLine 落在了那一行行尾。
        //
        // 必须伪造非重定向，否则 spinner 根本不画，统计行走的是另一条路径。
        var prevRedirected = SafeColor.IsRedirected;
        var prevSpinnerRedirected = AgentCore.IsOutputRedirected;
        var prevColumns = Environment.GetEnvironmentVariable("COLUMNS");
        SafeColor.IsRedirected = () => false;
        AgentCore.IsOutputRedirected = () => false;
        Environment.SetEnvironmentVariable("COLUMNS", "80");
        try
        {
            var outp = Capture(() => MakeAgent(new ReasoningThenCharProvider())
                .RunAsync("说点什么", CancellationToken.None));
            var screen = ReplayTerminal(outp);

            var reasoningAt = screen.IndexOf("思考内容", StringComparison.Ordinal);
            var statsAt = screen.IndexOf("用时", StringComparison.Ordinal);
            Assert.True(reasoningAt >= 0, $"应有思考文本: {screen}");
            Assert.True(statsAt >= 0, $"应有定格统计行: {screen}");
            // 思考与统计行之间必须**已经换行**：直接拼在一起就是真机那个症状
            var between = screen[reasoningAt..statsAt];
            Assert.Contains("\n", between);
            // 统计行不能在思考那一行上
            var reasoningLine = screen[..reasoningAt].Split('\n').Length;
            var statsLine = screen[..statsAt].Split('\n').Length;
            Assert.True(statsLine > reasoningLine, $"统计行必须独占总行: [{screen}]");
        }
        finally
        {
            SafeColor.IsRedirected = prevRedirected;
            AgentCore.IsOutputRedirected = prevSpinnerRedirected;
            Environment.SetEnvironmentVariable("COLUMNS", prevColumns);
        }
    }

    private static string StripControls(string s)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\x1b')
            {
                var j = i + 1;
                while (j < s.Length && !char.IsLetter(s[j]))
                    j++;
                i = j;
                continue;
            }
            if (s[i] == '\r')
                continue;
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>按 \r / ESC[2K / \n 的语义把输出回放到屏幕上，返回最终画面。</summary>
    private static string ReplayTerminal(string output)
    {
        var lines = new List<StringBuilder> { new() };
        var row = 0;
        var col = 0;
        for (var i = 0; i < output.Length; i++)
        {
            var c = output[i];
            if (c == '\r') { col = 0; continue; }
            if (c == '\x1b' && i + 1 < output.Length && output[i + 1] == '[')
            {
                var j = i + 2;
                while (j < output.Length && !char.IsLetter(output[j]))
                    j++;
                if (j < output.Length && output[j] == 'K')
                    lines[row].Clear();
                i = j;
                continue;
            }
            if (c == '\n')
            {
                if (row == lines.Count - 1)
                    lines.Add(new StringBuilder());
                row++;
                col = 0;
                continue;
            }
            var sb = lines[row];
            while (sb.Length < col)
                sb.Append(' ');
            if (col < sb.Length)
                sb[col] = c;
            else
                sb.Append(c);
            col++;
        }
        var all = new StringBuilder();
        foreach (var l in lines)
            all.Append(l).Append('\n');
        return all.ToString();
    }
}
