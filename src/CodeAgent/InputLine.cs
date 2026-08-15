using System.Text;

namespace CodeAgent;

/// <summary>
/// 终端输入行：支持 ↑/↓ 历史、TAB 命令补全（列出候选）、退格、Ctrl+L 清屏。
/// 采用保守的重绘方式：光标始终保持在行尾、不依赖 SetCursorPosition，
/// 重绘时去除提示符前导换行（避免每次按键产生额外空行），保证各终端稳定。
/// stdin 被重定向（管道/文件）时自动回退为 Console.ReadLine，保证脚本兼容。
/// </summary>
public static class InputLine
{
    private static readonly string HistoryFile =
        Path.Combine(Environment.CurrentDirectory, ".codeagent", "history.txt");

    /// <summary>可 TAB 补全的 REPL 命令。</summary>
    public static readonly string[] Commands =
    [
        "/help", "/clear", "/cls", "/model", "/config", "/session", "/setup",
        "/undo", "/diff", "/save", "/load", "/export", "/stats", "/retry",
        "/tools", "/providers", "/models", "/mode", "/exit", "/quit",
    ];

    private static readonly List<string> History = LoadHistory();
    private const int MaxHistory = 100;

    /// <summary>读取一行输入；EOF（重定向输入关闭）时返回 null。</summary>
    public static string? Read(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            Console.Write(prompt);
            var line = Console.ReadLine();
            if (line is null)
                return null;
            Remember(line);
            return line;
        }

        var buf = new StringBuilder();
        var session = new List<string>(History);
        var idx = session.Count;

        Console.Write(prompt);
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    var line = buf.ToString();
                    Remember(line);
                    return line;

                case ConsoleKey.Backspace:
                    if (buf.Length > 0)
                    {
                        buf.Length--;
                        Redraw(prompt, buf);
                    }
                    break;

                case ConsoleKey.UpArrow:
                    if (idx > 0)
                    {
                        idx--;
                        SetBuf(session, buf, idx);
                        Redraw(prompt, buf);
                    }
                    break;

                case ConsoleKey.DownArrow:
                    if (idx < session.Count)
                    {
                        idx++;
                        SetBuf(session, buf, idx);
                        Redraw(prompt, buf);
                    }
                    break;

                case ConsoleKey.Tab:
                    HandleTab(prompt, buf);
                    break;

                case ConsoleKey.Escape:
                    break; // 输入中按 ESC 无操作

                case ConsoleKey.L when (key.Modifiers & ConsoleModifiers.Control) != 0:
                    try { Console.Clear(); } catch { /* 忽略 */ }
                    Redraw(prompt, buf);
                    break;

                default:
                    if (key.KeyChar != '\0' && key.KeyChar != '\u0003')
                    {
                        buf.Append(key.KeyChar);
                        Redraw(prompt, buf);
                    }
                    break;
            }
        }
    }

    private static void SetBuf(List<string> session, StringBuilder buf, int idx)
    {
        buf.Clear();
        if (idx < session.Count)
            buf.Append(session[idx]);
    }

    /// <summary>
    /// 保守重绘：清掉当前行后重写「提示符+输入」。提示符去掉前导换行，
    /// 保持在当前行内重绘；光标固定在行尾，不依赖 SetCursorPosition。
    /// </summary>
    private static void Redraw(string prompt, StringBuilder buf)
    {
        try
        {
            var text = prompt.TrimStart('\n') + buf;
            Console.Write("\r" + text + new string(' ', Math.Max(1, Console.WindowWidth - text.Length)));
            Console.Write("\r" + text);
        }
        catch
        {
            Console.Write("\r" + prompt.TrimStart('\n') + buf);
        }
    }

    /// <summary>Tab 补全：唯一匹配直接补全；多个匹配列出候选命令；无匹配给提示。</summary>
    private static void HandleTab(string prompt, StringBuilder buf)
    {
        var line = buf.ToString();
        if (!line.StartsWith('/'))
            return;
        var matches = Commands.Where(c => c.StartsWith(line, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 1)
        {
            buf.Clear();
            buf.Append(matches[0]);
            Redraw(prompt, buf);
        }
        else if (matches.Count > 1)
        {
            Console.WriteLine();
            Console.WriteLine("  可用命令:");
            foreach (var m in matches)
                Console.Write($"  {m}");
            Console.WriteLine();
            Redraw(prompt, buf);
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("  （没有匹配的命令，输入 /help 查看全部）");
            Redraw(prompt, buf);
        }
    }

    private static void Remember(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        if (History.Count > 0 && History[^1] == line)
            return;
        History.Add(line);
        if (History.Count > MaxHistory)
            History.RemoveAt(0);
        SaveHistory();
    }

    private static List<string> LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryFile))
                return [];
            return File.ReadAllLines(HistoryFile)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .TakeLast(MaxHistory)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
            File.WriteAllLines(HistoryFile, History.TakeLast(MaxHistory));
        }
        catch
        {
            // 历史保存失败不影响主流程
        }
    }
}
