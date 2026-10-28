using System.Text;
using System.Text.Json.Nodes;
using CodeAgent.Tools;

namespace CodeAgent;

/// <summary>状态栏外部命令（学自 Claude Code 的 statusLine）。
///
/// 修的是一个真实断层：状态栏显示的段是**固定**的（模式/模型/目录/ctx），
/// 想加「当前分支有几个未提交改动」「CI 状态」这类信息只能改代码。
///
/// 三条硬约束，任何一条破了都不如不做：
/// 1. 命令不能拖死界面——超时即放弃，状态栏照常出；
/// 2. 输出必须能上状态栏——只取一行、去 ANSI、折叠空白、截断；
/// 3. 空间不够时它**第一个**被丢掉（在 BuildStatusBar 的 drops 里排第一），
///    不能为了用户脚本输出把 ctx/路径挤没。
/// </summary>
public static class StatusLine
{
    /// <summary>默认超时：状态栏是每轮都要画的，超过这个时间就该放弃。</summary>
    public const int DefaultTimeoutSeconds = 2;

    /// <summary>单段最长字符数：再长也只能占一行，没有意义反而挤掉别的段。</summary>
    public const int MaxSegmentChars = 120;

    /// <summary>把命令输出整理成一行状态栏片段。取第一条非空行，去掉 ANSI 转义与控制字符，折叠空白。</summary>
    public static string FormatSegment(string? raw, int maxChars = MaxSegmentChars)
    {
        if (string.IsNullOrEmpty(raw) || maxChars <= 0)
            return string.Empty;

        var line = FirstNonBlankLine(StripAnsi(raw));
        if (line.Length == 0)
            return string.Empty;

        var sb = new StringBuilder(line.Length);
        var lastWasSpace = false;
        foreach (var ch in line)
        {
            // 顺序很关键：\t 既是控制字符又是空白。若先按控制字符过滤，tab 会被**静默删掉**
            // （"ok\t3" 变成 "ok3"，两段数字粘成一个数）——先按空白折叠成空格才是对的。
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                    sb.Append(' ');
                lastWasSpace = true;
                continue;
            }
            if (char.IsControl(ch))
                continue;
            sb.Append(ch);
            lastWasSpace = false;
        }

        var text = sb.ToString().Trim();
        if (text.Length <= maxChars)
            return text;
        // 截断用单字符省略号：TextUtil.DisplayWidth 按显示宽度算，尾部一个「…」不会让调用方多算。
        return text[..Math.Max(1, maxChars - 1)] + "…";
    }

    /// <summary>构造给脚本的 JSON 载荷（经 stdin 传入）。字段名与状态栏段一致，便于脚本直接 echo。</summary>
    public static string BuildPayload(string mode, string model, string cwd, string? branch)
    {
        var payload = new JsonObject
        {
            ["mode"] = mode,
            ["model"] = model,
            ["cwd"] = cwd,
            ["branch"] = branch ?? string.Empty,
        };
        return payload.ToJsonString();
    }

    /// <summary>执行状态栏命令并返回一行片段。任何失败都返回空串——状态栏绝不能因此变丑或抛异常。</summary>
    public static async Task<string> RunAsync(string command, string shell, string cwd, int timeoutSeconds, string payloadJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return string.Empty;
        try
        {
            var (exitCode, stdout, _, timedOut) = await ShellRunner.CaptureAsync(
                shell, command, cwd, Math.Clamp(timeoutSeconds, 1, 30), payloadJson, null, ct).ConfigureAwait(false);
            // 退出码非 0 也照样显示 stdout：很多脚本用非 0 表示「有告警」，
            // 把它的输出藏掉等于让人以为脚本没生效。真正该藏的是超时与起不来。
            _ = exitCode;
            return timedOut ? string.Empty : FormatSegment(stdout);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string FirstNonBlankLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim('\r', ' ', '\t');
            if (trimmed.Length > 0)
                return trimmed;
        }
        return string.Empty;
    }

    /// <summary>去掉 CSI / OSC 等 ANSI 序列：把控制字符原样塞进状态栏会破坏整行宽度计算。</summary>
    private static string StripAnsi(string text)
    {
        if (text.IndexOf('\u001b') < 0)
            return text;
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch != '\u001b')
            {
                sb.Append(ch);
                i++;
                continue;
            }
            i++;
            if (i >= text.Length)
                break;
            var next = text[i];
            if (next == '[')
            {
                // CSI：ESC [ 参数 终止符（0x40-0x7E）
                i++;
                while (i < text.Length && (text[i] < 0x40 || text[i] > 0x7E))
                    i++;
                i++;
                continue;
            }
            if (next == ']')
            {
                // OSC：ESC ] ... BEL 或 ESC \\
                i++;
                while (i < text.Length && text[i] != '\a')
                {
                    if (text[i] == '\u001b' && i + 1 < text.Length && text[i + 1] == '\\')
                    {
                        i += 2;
                        goto done;
                    }
                    i++;
                }
                i++;
            done:
                continue;
            }
            i++; // 其它单字符转义：ESC + 1 字节，直接丢弃
        }
        return sb.ToString();
    }
}
