using System.Text;

namespace CodeAgent;

/// <summary>
/// 终端输入行：支持 ↑/↓ 历史、TAB 命令补全、行内编辑（方向键/Home/End/退格）、Ctrl+L 清屏。
/// stdin 被重定向（管道/文件）时自动回退为 Console.ReadLine，保证脚本与非交互场景兼容。
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
        "/tools", "/providers", "/mode", "/exit", "/quit",
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
        var cursor = 0;

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
                    if (cursor > 0)
                    {
                        buf.Remove(cursor - 1, 1);
                        cursor--;
                        Redraw(prompt, buf, cursor);
                    }
                    break;

                case ConsoleKey.Delete:
                    if (cursor < buf.Length)
                    {
                        buf.Remove(cursor, 1);
                        Redraw(prompt, buf, cursor);
                    }
                    break;

                case ConsoleKey.LeftArrow:
                    if (cursor > 0)
                    {
                        cursor--;
                        Console.SetCursorPosition(Math.Max(0, Console.CursorLeft - 1), Console.CursorTop);
                    }
                    break;

                case ConsoleKey.RightArrow:
                    if (cursor < buf.Length)
                    {
                        cursor++;
                        Console.SetCursorPosition(Console.CursorLeft + 1, Console.CursorTop);
                    }
                    break;

                case ConsoleKey.Home:
                    cursor = 0;
                    Redraw(prompt, buf, cursor);
                    break;

                case ConsoleKey.End:
                    cursor = buf.Length;
                    Redraw(prompt, buf, cursor);
                    break;

                case ConsoleKey.UpArrow:
                    if (idx > 0)
                    {
                        idx--;
                        SetBuffer(session, ref buf, ref cursor, idx);
                        Redraw(prompt, buf, cursor);
                    }
                    break;

                case ConsoleKey.DownArrow:
                    if (idx < session.Count)
                    {
                        idx++;
                        SetBuffer(session, ref buf, ref cursor, idx);
                        Redraw(prompt, buf, cursor);
                    }
                    break;

                case ConsoleKey.Tab:
                    var completion = TryComplete(buf.ToString());
                    if (completion is not null)
                    {
                        buf.Clear();
                        buf.Append(completion);
                        cursor = buf.Length;
                        Redraw(prompt, buf, cursor);
                    }
                    break;

                case ConsoleKey.Escape:
                    break; // 输入中按 ESC 无操作

                case ConsoleKey.L when (key.Modifiers & ConsoleModifiers.Control) != 0:
                    try { Console.Clear(); } catch { /* 忽略 */ }
                    Redraw(prompt, buf, cursor);
                    break;

                default:
                    if (key.KeyChar != '\0' && key.KeyChar != '\u0003')
                    {
                        buf.Insert(cursor, key.KeyChar);
                        cursor++;
                        Redraw(prompt, buf, cursor);
                    }
                    break;
            }
        }
    }

    private static void SetBuffer(List<string> session, ref StringBuilder buf, ref int cursor, int idx)
    {
        buf.Clear();
        if (idx < session.Count)
            buf.Append(session[idx]);
        cursor = buf.Length;
    }

    private static void Redraw(string prompt, StringBuilder buf, int cursor)
    {
        try
        {
            Console.Write("\r" + new string(' ', Math.Max(0, Console.WindowWidth - 1)));
            Console.Write("\r" + prompt + buf);
            var left = Console.CursorLeft - (buf.Length - cursor);
            Console.SetCursorPosition(Math.Max(0, left), Console.CursorTop);
        }
        catch
        {
            // 重绘失败时尽量保持可输入
        }
    }

    private static string? TryComplete(string line)
    {
        if (!line.StartsWith('/'))
            return null;
        var matches = Commands.Where(c => c.StartsWith(line, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1)
            return matches[0];
        if (matches.Count > 1)
        {
            var prefix = matches[0];
            foreach (var m in matches.Skip(1))
            {
                while (!m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && prefix.Length > 0)
                    prefix = prefix[..^1];
            }
            return prefix.Length > line.Length ? prefix : null;
        }
        return null;
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
