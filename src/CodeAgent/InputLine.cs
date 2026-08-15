using System.Text;

namespace CodeAgent;

/// <summary>
/// 终端输入行：↑/↓ 历史、斜杠命令菜单（**滚动式**：仅用 WriteLine/换行，不依赖光标定位与颜色，
/// 在任何终端都能工作）、TAB 补全、退格、Ctrl+L 清屏、Alt+M/U/D/N 快捷键。
/// stdin 被重定向（管道/文件）时自动回退为 Console.ReadLine，保证脚本兼容。
/// </summary>
public static class InputLine
{
    private static readonly string HistoryFile =
        Path.Combine(Environment.CurrentDirectory, ".codeagent", "history.txt");

    /// <summary>命令目录（名称 + 说明），用于菜单展示与补全。</summary>
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
    private const int MenuMaxRows = 8;

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

        var menuOpen = false;
        var modePicker = false;
        var menuItems = new List<(string Name, string Desc)>();
        var menuIndex = -1;
        var lastFilter = "";

        Console.Write(prompt);

        // 输入行重绘：仅用 \r，不依赖光标定位
        void RedrawInput()
        {
            Console.Write("\r" + promptPlain + buf + new string(' ', 4));
            Console.Write("\r" + promptPlain + buf);
        }

        // 完整列表：仅在打开/过滤变化时打印（编号 1-9，数字键可直接选中）
        void PrintMenuList()
        {
            Console.WriteLine();
            Console.WriteLine(modePicker
                ? "  Modes (1-9 switch, up/down select, Esc close):"
                : "  Commands (1-9 run, up/down select, Enter run, Esc close):");
            if (menuItems.Count == 0)
            {
                Console.WriteLine("  (no matching item)");
            }
            else
            {
                for (int i = 0; i < Math.Min(menuItems.Count, MenuMaxRows); i++)
                    Console.WriteLine($"  {i + 1}) {menuItems[i].Name,-16} {menuItems[i].Desc}");
                if (menuItems.Count > MenuMaxRows)
                    Console.WriteLine("  ... (more)");
            }
            Console.WriteLine();
            Console.Write(promptPlain + buf);
        }

        // 选中行：方向键/Tab 时换行打印，避免与提示符同行，也避免整块菜单刷屏
        void PrintSelection()
        {
            if (menuIndex >= 0 && menuIndex < menuItems.Count)
            {
                Console.WriteLine(); // 先换行，确保选中行从新的一行开始
                Console.WriteLine($"  → {menuItems[menuIndex].Name,-16} {menuItems[menuIndex].Desc}");
                Console.Write(promptPlain + buf);
            }
        }

        void RefreshMenu()
        {
            var pat = buf.ToString();
            if (pat.StartsWith('／'))
                pat = "/" + pat[1..];
            menuItems = Commands
                .Where(c => c.Name.StartsWith(pat, StringComparison.OrdinalIgnoreCase))
                .Select(c => c)
                .ToList();
            if (menuIndex >= menuItems.Count)
                menuIndex = menuItems.Count - 1;
            PrintMenuList();
            lastFilter = pat;
        }

        void OpenMenu(bool picker)
        {
            if (menuOpen)
                return;
            menuOpen = true;
            modePicker = picker;
            menuIndex = -1;
            if (picker)
            {
                menuItems = [.. modes ?? []];
                PrintMenuList();
                lastFilter = "/";
            }
            else
            {
                RefreshMenu();
            }
        }

        void CloseMenu()
        {
            menuOpen = false;
            menuItems.Clear();
            menuIndex = -1;
        }

        void OnTextChanged()
        {
            if (menuOpen && !modePicker)
            {
                var pat = buf.ToString();
                if (pat.StartsWith('／'))
                    pat = "/" + pat[1..];
                if (pat != lastFilter)
                    RefreshMenu();
                else
                    RedrawInput();
            }
            else
            {
                RedrawInput();
            }
        }

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            // 命令菜单：输入不再以斜杠开头 → 关闭；模式选择器不受输入影响
            if (menuOpen && !modePicker && !SlashLike(buf.ToString()))
                CloseMenu();

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    if (menuOpen && menuItems.Count > 0 && menuIndex >= 0)
                    {
                        var sel = menuItems[menuIndex].Name;
                        CloseMenu();
                        Console.WriteLine();
                        var submit = modePicker ? $"/mode {sel}" : sel;
                        Remember(submit);
                        return submit;
                    }
                    if (menuOpen)
                        CloseMenu(); // 未选择任何项：关闭菜单，按原输入提交
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
                        OnTextChanged();
                    }
                    break;

                case ConsoleKey.UpArrow:
                    if (menuOpen && menuItems.Count > 0)
                    {
                        menuIndex = menuIndex < 0 ? menuItems.Count - 1 : (menuIndex - 1 + menuItems.Count) % menuItems.Count;
                        PrintSelection();
                    }
                    else if (idx > 0)
                    {
                        idx--;
                        SetBuf(session, buf, idx);
                        RedrawInput();
                    }
                    break;

                case ConsoleKey.DownArrow:
                    if (menuOpen && menuItems.Count > 0)
                    {
                        menuIndex = menuIndex < 0 ? 0 : (menuIndex + 1) % menuItems.Count;
                        PrintSelection();
                    }
                    else if (idx < session.Count)
                    {
                        idx++;
                        SetBuf(session, buf, idx);
                        RedrawInput();
                    }
                    break;

                case ConsoleKey.Tab:
                    if (!menuOpen && SlashLike(buf.ToString()))
                        OpenMenu(false);
                    else if (menuOpen && menuItems.Count > 1)
                    {
                        menuIndex = menuIndex < 0 ? 0 : (menuIndex + 1) % menuItems.Count;
                        PrintSelection();
                    }
                    break;

                case ConsoleKey.D1 or ConsoleKey.D2 or ConsoleKey.D3 or ConsoleKey.D4 or ConsoleKey.D5
                    or ConsoleKey.D6 or ConsoleKey.D7 or ConsoleKey.D8 or ConsoleKey.D9:
                    // 数字键直接选中执行（菜单编号 1-9）
                    if (menuOpen && menuItems.Count > 0)
                    {
                        var n = key.Key - ConsoleKey.D1 + 1;
                        if (n <= menuItems.Count)
                        {
                            var sel = menuItems[n - 1].Name;
                            CloseMenu();
                            Console.WriteLine();
                            var submit = modePicker ? $"/mode {sel}" : sel;
                            Remember(submit);
                            return submit;
                        }
                    }
                    break;

                case ConsoleKey.Escape:
                    if (menuOpen)
                    {
                        CloseMenu();
                        Console.WriteLine("  (menu closed)");
                        Console.Write(promptPlain + buf);
                    }
                    break;

                case ConsoleKey.M when IsShortcut(key) && modes is { Count: > 0 }:
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

                case ConsoleKey.L when (key.Modifiers & ConsoleModifiers.Control) != 0:
                    try { Console.Clear(); } catch { /* 忽略 */ }
                    RedrawInput();
                    if (menuOpen)
                        RefreshMenu();
                    break;

                default:
                    if (key.KeyChar != '\0' && key.KeyChar != '\u0003' && !char.IsControl(key.KeyChar))
                    {
                        if (menuOpen && modePicker)
                            CloseMenu();
                        buf.Append(key.KeyChar);
                        OnTextChanged();
                        if (!modePicker && SlashLike(buf.ToString()) && !menuOpen)
                            OpenMenu(false);
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

    /// <summary>输入是否以斜杠开头（兼容中文输入法的全角 ／）。</summary>
    private static bool SlashLike(string s) => s.StartsWith('/') || s.StartsWith('／');

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
