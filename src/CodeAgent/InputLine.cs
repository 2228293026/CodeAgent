using System.Text;

namespace CodeAgent;

/// <summary>
/// 终端输入行：斜杠命令菜单（**ANSI 原地渲染**：方向键让 ">" 在列表内上下移动，
/// 纯转义文本输出，不依赖控制台光标 API，兼容 Windows Terminal 等现代终端；
/// `tuiAnsi=false` 时退回滚动式）、数字键 1-9 直接执行、↑/↓ 历史、TAB 补全、
/// 退格、Ctrl+L 清屏、Alt+M/U/D/N 快捷键。
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
        ("/thinking", "Adjust model thinking effort"),
        ("/mode", "View or switch work mode"),
        ("/exit", "Exit (also /quit)"),
        ("/quit", "Exit"),
    ];

    private static readonly List<string> History = LoadHistory();
    private const int MaxHistory = 100;
    private const int MenuMaxRows = 8;

    // —— ANSI 转义辅助（纯文本输出，任何现代终端都支持） ——
    private static void Ansi(string s) => Console.Write("\x1b[" + s);
    private static void AnsiUp(int n) { if (n > 0) Ansi(n + "A"); }
    private static void AnsiDown(int n) { if (n > 0) Ansi(n + "B"); }
    private static void AnsiCol1() => Ansi("1G");
    private static void AnsiErase() => Ansi("K");

    /// <summary>读取终端宽度；失败返回 0（未知）。</summary>
    private static int TryWindowWidth()
    {
        try { return Math.Clamp(Console.WindowWidth, 0, 300); } catch { return 0; }
    }

    /// <summary>读取一行输入；EOF（重定向输入关闭）时返回 null。modes 用于 Alt+M 模式菜单，ansi 控制菜单渲染方式。</summary>
    public static string? Read(string prompt, IReadOnlyList<(string Name, string Desc)>? modes = null, bool ansi = true)
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
        var draft = (string?)null; // 浏览历史前的原始输入草稿（↓ 回到底部时恢复）
        var promptPlain = prompt.TrimStart('\n');

        var winW = TryWindowWidth();
        var ansiOk = ansi && winW >= 30; // 宽度未知或太窄时退回滚动式，避免换行破坏 ANSI 行号计算
        string Fit(string s)
        {
            var max = Math.Max(10, winW - 4);
            return s.Length > max ? s[..max] + "…" : s;
        }

        var menuOpen = false;
        var modePicker = false;
        var menuItems = new List<(string Name, string Desc)>();
        var menuIndex = -1;
        var lastFilter = "";
        var menuShown = 0;   // 可见项数
        var menuOffset = 0;  // 可见窗口在列表中的起点
        var menuRows = 0;    // 已绘制的菜单块行数（擦除用）
        var menuEverPainted = false; // 菜单块是否已建立（首次在输入行下方建立，之后原地刷新）
        var menuListShown = false;   // 滚动模式下完整列表是否已打印（之后过滤变化只打单行）

        Console.Write(prompt);

        // —— 绘制助手（局部函数） ——

        int MenuAbove() => menuShown + 3; // header + 项 + more 行 + 空行

        string Header() => modePicker
            ? "  Modes (up/down select, Enter switch, Esc close):"
            : "  Commands (1-9 run, up/down select, Enter run, Esc close):";

        void ScrollInput()
        {
            if (ansiOk)
            {
                // 支持 ANSI：\x1b[K 清到行尾（单次写入，避免逐次 Write 卡顿）
                Console.Write("\r" + promptPlain + buf + "\x1b[K");
            }
            else
            {
                Console.Write("\r" + promptPlain + buf + new string(' ', 4));
                Console.Write("\r" + promptPlain + buf);
            }
        }

        void RedrawInput()
        {
            if (menuOpen && ansiOk)
            {
                // 单次写入：回到行首 + 清到行尾 + 重写输入
                Console.Write("\x1b[1G\x1b[K" + promptPlain + buf);
                return;
            }
            ScrollInput();
        }

        // —— ANSI 原地渲染（默认） ——
        void PrintListAnsi()
        {
            menuShown = Math.Min(menuItems.Count, MenuMaxRows);
            if (menuOffset > menuItems.Count - menuShown)
                menuOffset = Math.Max(0, menuItems.Count - menuShown);
            var above = menuShown + 3; // header + 项 + more/空态行 + 空行
            var first = !menuEverPainted;
            var sb = new StringBuilder();
            if (first)
            {
                // 首次：在输入行下方建立菜单块（此后输入行固定在块底部）
                sb.Append('\n');
            }
            else
            {
                // 刷新：回到块顶部，在原地重绘（输入行不移动）
                sb.Append($"\x1b[{above}A\x1b[1G");
            }
            sb.AppendLine(Fit(Header()));
            if (menuItems.Count == 0)
            {
                sb.AppendLine(Fit("  (no matching item, press Esc to close)"));
            }
            else
            {
                for (int i = 0; i < menuShown; i++)
                {
                    var k = menuOffset + i;
                    var line = Fit($"  {i + 1}) {menuItems[k].Name,-16} {menuItems[k].Desc}");
                    // 选中项：反显高亮（\x1b[7m），缩进不变，不用 > 标记
                    sb.AppendLine(k == menuIndex ? "\x1b[7m" + line + "\x1b[0m" : line);
                }
                sb.AppendLine(Fit(menuItems.Count > menuShown ? $"  ... (+{menuItems.Count - menuShown} more)" : ""));
            }
            sb.AppendLine(); // 空行；末尾换行后光标已在输入行
            sb.Append("\x1b[1G");
            sb.Append(promptPlain + buf);
            sb.Append("\x1b[K");
            menuEverPainted = true;
            menuRows = above;
            Console.Write(sb.ToString());
        }

        // 擦除菜单块（rows 行），回到输入行——单次写入
        void EraseMenuAnsi(int rows)
        {
            var sb = new StringBuilder();
            sb.Append($"\x1b[{rows}A");
            for (int i = 0; i < rows; i++)
            {
                sb.Append("\x1b[K\x1b[1B");
            }
            sb.Append("\x1b[1G");
            sb.Append(promptPlain + buf);
            sb.Append("\x1b[K");
            menuRows = 0;
            Console.Write(sb.ToString());
        }

        void MoveSelectionAnsi(int oldIndex, int newIndex)
        {
            if (newIndex < menuOffset || newIndex >= menuOffset + menuShown)
            {
                // 窗口滚动：显式更新偏移，原地重绘整个块（行高不变，直接覆盖旧内容）
                menuOffset = newIndex < menuOffset
                    ? newIndex
                    : Math.Max(0, newIndex - menuShown + 1);
                menuOffset = Math.Min(menuOffset, Math.Max(0, menuItems.Count - menuShown));
                PrintListAnsi();
                return;
            }
            // 整段拼接后单次写入：擦旧行 → 写新行 → 回到输入行
            var sb = new StringBuilder();
            if (oldIndex >= menuOffset && oldIndex < menuOffset + menuShown && oldIndex != newIndex)
            {
                var up = MenuAbove() - 1 - (oldIndex - menuOffset);
                sb.Append($"\x1b[{up}A\x1b[1G\x1b[K");
                sb.Append(Fit($"  {oldIndex - menuOffset + 1}) {menuItems[oldIndex].Name,-16} {menuItems[oldIndex].Desc}"));
                sb.Append($"\x1b[{up}B");
            }
            var up2 = MenuAbove() - 1 - (newIndex - menuOffset);
            sb.Append($"\x1b[{up2}A\x1b[1G\x1b[K");
            sb.Append("\x1b[7m" + Fit($"  {newIndex - menuOffset + 1}) {menuItems[newIndex].Name,-16} {menuItems[newIndex].Desc}") + "\x1b[0m");
            sb.Append($"\x1b[{up2}B\x1b[1G");
            sb.Append(promptPlain + buf);
            sb.Append("\x1b[K");
            Console.Write(sb.ToString());
        }

        // —— 滚动式渲染（tuiAnsi=false 时的兜底） ——
        void PrintListScroll()
        {
            menuListShown = true;
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine(Fit(Header()));
            // 紧凑展示：一行列出全部命令名（避免 14 行大块渲染导致卡顿）
            sb.AppendLine(Fit("  " + string.Join(" ", Commands.Select(c => c.Name))));
            sb.AppendLine();
            sb.Append(promptPlain + buf);
            Console.Write(sb.ToString());
        }

        // 过滤变化时单行显示带编号的匹配结果（可直接按数字执行）
        void PrintFilterScroll()
        {
            var sb = new StringBuilder();
            if (menuItems.Count == 0)
            {
                sb.AppendLine(Fit("  (no matching item, press Esc to close)"));
            }
            else
            {
                var parts = menuItems.Take(6).Select((m, i) => $"{i + 1}) {m.Name}");
                var more = menuItems.Count > 6 ? $" …(共 {menuItems.Count} 项)" : "";
                sb.AppendLine(Fit($"  [{buf}] {string.Join(" ", parts)}{more}"));
            }
            sb.AppendLine();
            sb.Append(promptPlain + buf);
            Console.Write(sb.ToString());
        }

        void MoveSelectionScroll(int newIndex)
        {
            if (menuItems.Count == 0)
                return;
            menuIndex = newIndex;
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine(Fit($"  → {menuItems[menuIndex].Name,-16} {menuItems[menuIndex].Desc}"));
            sb.Append(promptPlain + buf);
            Console.Write(sb.ToString());
        }

        void PrintMenu()
        {
            if (ansiOk)
                PrintListAnsi();
            else
                PrintListScroll();
        }

        void RefreshMenu()
        {
            var pat = buf.ToString();
            if (pat.StartsWith('／'))
                pat = "/" + pat[1..];
            var newItems = Commands
                .Where(c => c.Name.StartsWith(pat, StringComparison.OrdinalIgnoreCase))
                .Select(c => c)
                .ToList();
            // 仅当过滤结果真的变化时才重绘，避免 /m→/mo→/mod 每个按键都刷一块菜单
            var same = newItems.Count == menuItems.Count &&
                       newItems.Zip(menuItems, (a, b) => a.Name == b.Name).All(x => x);
            if (!same)
            {
                menuItems = newItems;
                if (menuIndex >= menuItems.Count)
                    menuIndex = menuItems.Count - 1;
                if (ansiOk)
                {
                    // 仅当菜单已绘制过才擦除（首次打开直接建立，避免擦掉状态栏等上方内容）；
                    // 擦除高度取已绘制与新块最大值（过滤变宽时不覆盖上方）
                    if (menuRows > 0)
                        EraseMenuAnsi(Math.Max(menuRows, Math.Min(menuItems.Count, MenuMaxRows) + 3));
                    PrintListAnsi();
                }
                else if (!menuListShown)
                {
                    PrintListScroll(); // 打开：完整编号列表
                }
                else
                {
                    PrintFilterScroll(); // 过滤变化：单行匹配结果
                }
            }
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
                PrintMenu();
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
            modePicker = false; // 必须重置，否则后续 / 命令菜单永远打不开
            if (ansiOk)
            {
                EraseMenuAnsi(menuRows);
                menuEverPainted = false; // 下次打开重新在输入行下方建立块
            }
            menuListShown = false;
        }

        void MoveSelection(int newIndex)
        {
            if (menuItems.Count == 0)
                return;
            var oldIndex = menuIndex;
            menuIndex = newIndex;
            if (ansiOk)
                MoveSelectionAnsi(oldIndex, newIndex);
            else
                MoveSelectionScroll(newIndex);
        }

        void OnTextChanged()
        {
            if (menuOpen && !modePicker && !SlashLike(buf.ToString()))
            {
                // 输入不再以斜杠开头（如退格删掉 /）：关闭并擦除菜单
                CloseMenu();
                RedrawInput();
                return;
            }
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

        // —— 主输入循环 ——
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
                        MoveSelection(menuIndex < 0 ? menuItems.Count - 1 : (menuIndex - 1 + menuItems.Count) % menuItems.Count);
                    }
                    else
                    {
                        if (menuOpen)
                            CloseMenu(); // 0 匹配的菜单无意义：关闭后浏览历史
                        if (idx > 0)
                        {
                            if (draft is null)
                                draft = buf.ToString(); // 记住浏览历史前的草稿
                            idx--;
                            SetBuf(session, buf, idx);
                            RedrawInput();
                        }
                    }
                    break;

                case ConsoleKey.DownArrow:
                    if (menuOpen && menuItems.Count > 0)
                    {
                        MoveSelection(menuIndex < 0 ? 0 : (menuIndex + 1) % menuItems.Count);
                    }
                    else
                    {
                        if (menuOpen)
                            CloseMenu();
                        if (idx < session.Count)
                        {
                            idx++;
                            if (idx == session.Count && draft is not null)
                            {
                                // 回到草稿（浏览历史前的原始输入）
                                buf.Clear();
                                buf.Append(draft);
                                draft = null;
                            }
                            else
                            {
                                SetBuf(session, buf, idx);
                            }
                            RedrawInput();
                        }
                    }
                    break;

                case ConsoleKey.Tab when (key.Modifiers & ConsoleModifiers.Shift) != 0:
                    // Shift+Tab：菜单内反向循环选择；菜单外切换到下一个模式
                    if (menuOpen && menuItems.Count > 1)
                    {
                        MoveSelection(menuIndex < 0 ? 0 : (menuIndex - 1 + menuItems.Count) % menuItems.Count);
                    }
                    else if (!menuOpen)
                    {
                        CloseMenu();
                        Console.WriteLine();
                        Remember("/mode next");
                        return "/mode next"; // Shift+Tab：切换 agent 模式
                    }
                    break;

                case ConsoleKey.Tab:
                    if (menuOpen && menuItems.Count == 1)
                    {
                        // 唯一匹配：Tab 补全为完整命令（/think + Tab → /thinking）
                        buf.Clear();
                        buf.Append(menuItems[0].Name);
                        draft = null;
                        CloseMenu();
                        RedrawInput();
                    }
                    else if (!menuOpen && SlashLike(buf.ToString()))
                    {
                        OpenMenu(false);
                    }
                    else if (menuOpen && menuItems.Count > 1)
                    {
                        // 多个匹配：循环选择
                        MoveSelection(menuIndex < 0 ? 0 : (menuIndex + 1) % menuItems.Count);
                    }
                    break;

                case ConsoleKey.D1 or ConsoleKey.D2 or ConsoleKey.D3 or ConsoleKey.D4 or ConsoleKey.D5
                    or ConsoleKey.D6 or ConsoleKey.D7 or ConsoleKey.D8 or ConsoleKey.D9:
                    // 命令菜单：数字键直接执行（1-9）；模式菜单不拦截数字（关闭并按普通输入，避免误触发切换）
                    if (menuOpen && !modePicker && menuItems.Count > 0)
                    {
                        var n = key.Key - ConsoleKey.D1 + 1;
                        if (n <= menuItems.Count)
                        {
                            var selIdx = Math.Min(menuOffset + n - 1, menuItems.Count - 1); // 窗口编号 -> 列表下标
                            var sel = menuItems[selIdx].Name;
                            CloseMenu();
                            Console.WriteLine();
                            Remember(sel);
                            return sel;
                        }
                        break;
                    }
                    if (menuOpen && modePicker)
                        CloseMenu(); // 模式菜单：数字按普通输入处理
                    if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                    {
                        buf.Append(key.KeyChar);
                        draft = null;
                        RedrawInput();
                    }
                    break;

                case ConsoleKey.Escape:
                    if (menuOpen)
                    {
                        CloseMenu();
                        if (!ansiOk)
                        {
                            Console.WriteLine("  (menu closed)");
                            Console.Write(promptPlain + buf);
                        }
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
                        draft = null; // 输入使草稿失效
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
