using System.Text;

namespace CodeAgent;

/// <summary>
/// 终端输入行：支持 ↑/↓ 历史、斜杠命令菜单（输入 / 自动弹出，方向键选择，回车执行）、
/// TAB 补全、退格、Ctrl+L 清屏。重绘保持在当前行、光标行尾固定，不依赖脆弱的换行重绘；
/// 菜单行使用 SetCursorPosition + 纯 ASCII 内容，保证各终端稳定。
/// stdin 被重定向（管道/文件）时自动回退为 Console.ReadLine，保证脚本兼容。
/// </summary>
public static class InputLine
{
    private static readonly string HistoryFile =
        Path.Combine(Environment.CurrentDirectory, ".codeagent", "history.txt");

    /// <summary>命令目录（名称 + 说明），用于菜单展示与补全。说明保持 ASCII 以兼容光标定位。</summary>
    public static readonly (string Name, string Desc)[] Commands =
    [
        ("/help", "Show help"),
        ("/clear", "Clear conversation history"),
        ("/cls", "Clear screen (or Ctrl+L)"),
        ("/model", "View or switch model"),
        ("/config", "Show config"),
        ("/session", "Show session log path"),
        ("/setup", "Run provider setup wizard"),
        ("/undo", "Undo last file change"),
        ("/diff", "Show diff of last change"),
        ("/save", "Save session snapshot"),
        ("/load", "Load a saved session"),
        ("/export", "Export session to Markdown"),
        ("/stats", "Show token usage stats"),
        ("/retry", "Re-run last request"),
        ("/tools", "List available tools"),
        ("/providers", "List configured providers"),
        ("/models", "List available models"),
        ("/mode", "View or switch work mode"),
        ("/exit", "Exit (also /quit)"),
        ("/quit", "Exit"),
    ];

    private static readonly List<string> History = LoadHistory();
    private const int MaxHistory = 100;
    private const int MenuMaxRows = 10;

    /// <summary>读取一行输入；EOF（重定向输入关闭）时返回 null。modes 用于 Alt+M 模式选择菜单。</summary>
    public static string? Read(string prompt, IReadOnlyList<(string Name, string Desc)>? modes = null)
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

        var promptPlain = prompt.TrimStart('\n');
        Console.Write(prompt);
        var inputRow = Console.CursorTop;

        var menuOpen = false;
        var modePicker = false;
        var menuItems = new List<(string Name, string Desc)>();
        var menuIndex = 0;
        var menuTop = 0;

        // —— 绘制助手（局部函数） ——
        void RedrawInput()
        {
            try
            {
                Console.SetCursorPosition(0, inputRow);
                Console.Write(promptPlain + buf + new string(' ', 4));
                Console.SetCursorPosition(0, inputRow);
                Console.Write(promptPlain + buf);
            }
            catch { /* 终端异常时忽略 */ }
        }

        void PaintMenu()
        {
            try
            {
                var shown = Math.Min(menuItems.Count, Math.Min(MenuMaxRows, Math.Max(1, Console.WindowHeight - menuTop - 1)));
                for (int i = 0; i < shown; i++)
                {
                    var (name, desc) = menuItems[i];
                    var line = $"  {(i == menuIndex ? ">" : " ")} {name,-16} {desc}";
                    Console.SetCursorPosition(0, menuTop + i);
                    if (i == menuIndex)
                    {
                        Console.BackgroundColor = ConsoleColor.DarkGray;
                        Console.ForegroundColor = ConsoleColor.White;
                    }
                    Console.Write(line.PadRight(Math.Max(1, Console.WindowWidth - 1)));
                    Console.ResetColor();
                }
                if (menuItems.Count > shown)
                {
                    Console.SetCursorPosition(0, menuTop + shown);
                    Console.Write(new string(' ', Math.Max(1, Console.WindowWidth - 1)));
                    Console.SetCursorPosition(0, menuTop + shown);
                    Console.Write("  ... (more)");
                }
                Console.SetCursorPosition(0, inputRow);
                Console.Write(promptPlain + buf);
            }
            catch { /* 终端异常时忽略 */ }
        }

        void CloseMenu()
        {
            if (!menuOpen)
                return;
            try
            {
                var shown = Math.Min(menuItems.Count, Math.Min(MenuMaxRows, Math.Max(1, Console.WindowHeight - menuTop - 1)));
                for (int i = 0; i < shown; i++)
                {
                    Console.SetCursorPosition(0, menuTop + i);
                    Console.Write(new string(' ', Math.Max(1, Console.WindowWidth - 1)));
                }
                Console.SetCursorPosition(0, inputRow);
                Console.Write(promptPlain + buf + new string(' ', 4));
                Console.SetCursorPosition(0, inputRow);
                Console.Write(promptPlain + buf);
            }
            catch { /* 终端异常时忽略 */ }
            menuOpen = false;
            menuItems.Clear();
        }

        void RefreshMenu()
        {
            menuItems = Commands
                .Where(c => c.Name.StartsWith(buf.ToString(), StringComparison.OrdinalIgnoreCase))
                .Select(c => c)
                .ToList();
            if (menuIndex >= menuItems.Count)
                menuIndex = Math.Max(0, menuItems.Count - 1);
            if (menuItems.Count == 0)
            {
                CloseMenu();
                return;
            }
            PaintMenu();
        }

        void OpenMenu(bool picker)
        {
            if (menuOpen)
                return;
            menuOpen = true;
            modePicker = picker;
            menuTop = inputRow + 1;
            menuIndex = 0;
            if (picker)
                PaintMenu();
            else
                RefreshMenu();
        }

        // —— 主输入循环 ——
        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            // 命令菜单：输入不再以 / 开头 → 关闭；模式选择器不受输入影响
            if (menuOpen && !modePicker && !buf.ToString().StartsWith('/'))
                CloseMenu();

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    if (menuOpen && menuItems.Count > 0)
                    {
                        var sel = menuItems[Math.Min(menuIndex, menuItems.Count - 1)].Name;
                        CloseMenu();
                        Console.WriteLine();
                        var submit = modePicker ? $"/mode {sel}" : sel;
                        Remember(submit);
                        return submit;
                    }
                    Console.WriteLine();
                    var line = buf.ToString();
                    Remember(line);
                    return line;

                case ConsoleKey.Backspace:
                    if (menuOpen && modePicker)
                        CloseMenu();
                    if (buf.Length > 0)
                    {
                        buf.Length--;
                        RedrawInput();
                        if (menuOpen && !modePicker)
                            RefreshMenu();
                    }
                    break;

                case ConsoleKey.UpArrow:
                    if (menuOpen && menuItems.Count > 0)
                    {
                        menuIndex = (menuIndex - 1 + menuItems.Count) % menuItems.Count;
                        PaintMenu();
                    }
                    else if (idx > 0)
                    {
                        idx--;
                        SetBuf(session, buf, idx);
                        RedrawInput();
                        if (menuOpen)
                            RefreshMenu();
                    }
                    break;

                case ConsoleKey.DownArrow:
                    if (menuOpen && menuItems.Count > 0)
                    {
                        menuIndex = (menuIndex + 1) % menuItems.Count;
                        PaintMenu();
                    }
                    else if (idx < session.Count)
                    {
                        idx++;
                        SetBuf(session, buf, idx);
                        RedrawInput();
                        if (menuOpen)
                            RefreshMenu();
                    }
                    break;

                case ConsoleKey.Tab:
                    if (!menuOpen && buf.ToString().StartsWith('/'))
                        OpenMenu(false);
                    else if (menuOpen && menuItems.Count > 1)
                    {
                        menuIndex = (menuIndex + 1) % menuItems.Count;
                        PaintMenu();
                    }
                    break;

                case ConsoleKey.Escape:
                    if (menuOpen)
                        CloseMenu();
                    break;

                case ConsoleKey.L when (key.Modifiers & ConsoleModifiers.Control) != 0:
                    try { Console.Clear(); } catch { /* 忽略 */ }
                    inputRow = 0;
                    RedrawInput();
                    if (menuOpen)
                    {
                        menuTop = inputRow + 1;
                        RefreshMenu();
                    }
                    break;

                case ConsoleKey.M when IsShortcut(key) && modes is { Count: > 0 }:
                    menuItems = [.. modes];
                    OpenMenu(true); // Alt+M / Ctrl+Shift+M：模式选择菜单
                    break;

                case ConsoleKey.U when IsShortcut(key):
                    CloseMenu();
                    Console.WriteLine();
                    Remember("/undo");
                    return "/undo"; // Alt+U / Ctrl+Shift+U：撤销最近一次修改

                case ConsoleKey.D when IsShortcut(key):
                    CloseMenu();
                    Console.WriteLine();
                    Remember("/diff");
                    return "/diff"; // Alt+D / Ctrl+Shift+D：查看最近修改的 diff

                case ConsoleKey.N when IsShortcut(key):
                    CloseMenu();
                    Console.WriteLine();
                    Remember("/clear");
                    return "/clear"; // Alt+N / Ctrl+Shift+N：新建会话（清空历史）

                default:
                    if (key.KeyChar != '\0' && key.KeyChar != '\u0003' && !char.IsControl(key.KeyChar))
                    {
                        if (menuOpen && modePicker)
                            CloseMenu();
                        buf.Append(key.KeyChar);
                        RedrawInput();
                        if (!modePicker && buf.ToString().StartsWith('/'))
                        {
                            if (!menuOpen)
                                OpenMenu(false);
                            else
                                RefreshMenu();
                        }
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

    /// <summary>快捷键判定：Alt+键 或 Ctrl+Shift+键（部分终端会吞 Alt，提供 Ctrl+Shift 兜底）。</summary>
    private static bool IsShortcut(ConsoleKeyInfo key)
    {
        var m = key.Modifiers;
        return (m & ConsoleModifiers.Alt) != 0 ||
               ((m & ConsoleModifiers.Control) != 0 && (m & ConsoleModifiers.Shift) != 0);
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
