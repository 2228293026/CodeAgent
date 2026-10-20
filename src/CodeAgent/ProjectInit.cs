using System.Text;

namespace CodeAgent;

/// <summary>
/// <c>/init</c>：扫描当前项目，生成一份 <c>AGENTS.md</c> 草稿。
///
/// 学自 Claude Code 的 <c>/init</c>。它解决的是一个真实断层：
/// 第 58 轮起 <c>AGENTS.md</c> 的内容会真的注入系统提示，
/// 但用户没有任何**生成**它的途径——只能自己从零手写，
/// 而「该写什么」正是模板存在的意义。
///
/// 刻意只做**确定性探测**，不让模型参与：
///   · <c>/init</c> 应当是瞬间完成、结果可预期的，不是又一次「等着流式输出」；
///   · 探测结果可测（纯函数），模型生成的结果不可测；
///   · 这份草稿是**起点**而不是成品——文件里明写「按需补充」比假装完备好。
///
/// 全部探测结果都来自文件名与少量文本内容，不解析完整工程模型：
/// 猜错一个构建命令比不写更糟（用户会照着跑，然后怀疑整个工具）。
/// 因此只在**证据充分**时才写某一项。
/// </summary>
public static class ProjectInit
{
    /// <summary>生成结果：文件内容 + 探测到的要点（供命令行回显「检测到什么」）。</summary>
    public sealed record Result(string Content, IReadOnlyList<string> Findings);

    /// <summary>草稿要写的文件名。
    ///
    /// 它必须与运行时读规则文件的**第一优先级**是同一个名字：
    /// 若 <c>/init</c> 写 <c>AGENTS.md</c>、运行时却先读别的，用户会得到
    /// 「生成成功、但下次启动一点用都没有」——最难查的那种缺陷。
    /// 所以这里只有一个常量，并由测试断言 <c>Program.RuleFileNames[0]</c> 与它相等。
    /// </summary>
    public const string TargetFileName = "AGENTS.md";

    /// <summary>落盘前的三种局面。
    ///
    /// 「文件存在吗」这一个问题答不了「写下去会发生什么」：
    /// AGENTS.md 不存在**不等于**可以安全生成。
    /// </summary>
    public enum TargetState
    {
        /// <summary>一个规则文件都没有：写下去只会新增，不会盖住任何东西。</summary>
        Create,

        /// <summary>AGENTS.md 已经在（文件**或目录**）：不能写。
        /// 目录同名时 <c>WriteAllText</c> 会抛 <c>UnauthorizedAccessException</c>，
        /// 所以判定必须用 File.Exists **或** Directory.Exists。</summary>
        Exists,

        /// <summary>AGENTS.md 不存在，但 <c>CLAUDE.md</c> / <c>.cursorrules</c> 在。
        ///
        /// 这是最危险的一种：新建的 AGENTS.md 排在名单第一位，会把它整个盖住——
        /// 用户原有的规则**一个字都读不到**，而屏幕上写着「已生成」。
        /// 文件没坏、程序没报错，只是规则静默失效了。</summary>
        Shadow,
    }

    /// <summary>一次 <c>/init</c> 落盘判定的完整结论。
    ///
    /// 把「该不该写」与「为什么」一起算出来，调用方只负责显示：
    /// 若让调用方自己拼原因，文案就会跟着实现各改各的，最后说的和做的对不上。</summary>
    /// <param name="State">三种局面。</param>
    /// <param name="Target">草稿的完整路径（已 <c>Create</c> 时就是要写的那个文件）。</param>
    /// <param name="Candidates">判定所依据的规则文件名单，按优先级排序。</param>
    /// <param name="Existing">
    /// 挡路的文件，按名单顺序：
    /// <c>Exists</c> 时是 <see cref="TargetFileName"/> 自己；
    /// <c>Shadow</c> 时是会被它盖住的那几份。
    /// </param>
    public sealed record TargetPlan(
        TargetState State,
        string Target,
        IReadOnlyList<string> Candidates,
        IReadOnlyList<string> Existing)
    {
        /// <summary>挡路的那一个（用于文案）；没有挡路的返回 <c>null</c> 而不是空串。</summary>
        public string? Blocking => Existing.Count > 0 ? Existing[0] : null;

        /// <summary>能不能直接写。只有这一种局面允许落盘。</summary>
        public bool CanWrite => State == TargetState.Create;
    }

    /// <summary>
    /// 判断在 <paramref name="root"/> 下写 <see cref="TargetFileName"/> 会发生什么。纯逻辑：只读文件系统，不写。
    ///
    /// 名单由调用方传入（<c>Program.RuleFileNames</c>），这里**不自己维护一份**：
    /// 两处各写一份名单，改一处漏一处就会出现「/init 认为安全、运行时却另有优先级」的漂移。
    /// </summary>
    public static TargetPlan EvaluateTarget(string root, IReadOnlyList<string> ruleFileNames)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("root 不能为空", nameof(root));

        var candidates = ruleFileNames is { Count: > 0 } ? ruleFileNames : new[] { TargetFileName };
        var target = Path.Combine(root, TargetFileName);

        // 同名目录也算「已存在」：WriteAllText 到目录会抛异常，
        // 而只判 File.Exists 的话这里会放行、然后在写入时崩给用户看。
        if (File.Exists(target) || Directory.Exists(target))
            return new TargetPlan(TargetState.Exists, target, candidates, new[] { TargetFileName });

        // 名单里排在目标**之后**的才算会被遮蔽；排在前面的比它优先，不受影响。
        // 名单里找不到目标时按「它排第一」处理（宁可拒绝，不要静默盖掉别人的规则）。
        var targetIndex = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (string.Equals(candidates[i], TargetFileName, StringComparison.Ordinal))
            {
                targetIndex = i;
                break;
            }
        }

        var shadowed = new List<string>();
        for (var i = targetIndex + 1; i < candidates.Count; i++)
        {
            var name = candidates[i];
            var path = Path.Combine(root, name);
            if (File.Exists(path) || Directory.Exists(path))
                shadowed.Add(name);
        }

        return shadowed.Count > 0
            ? new TargetPlan(TargetState.Shadow, target, candidates, shadowed)
            : new TargetPlan(TargetState.Create, target, candidates, Array.Empty<string>());
    }

    /// <summary>
    /// 把判定结论翻成给人看的话：第一行是**结论**（走 notice 样式），其余是**解释与出路**（走 hint 样式）。
    ///
    /// 拒绝文案有一条硬约束：只说「新文件会盖住它」，**不许**说旧文件「读不到 / 是空的」。
    /// 旧文件可能写得好好的、正在生效；说它坏了，用户就会去修一个根本没坏的东西。
    /// </summary>
    public static IReadOnlyList<string> ExplainTarget(TargetPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var lines = new List<string>();
        switch (plan.State)
        {
            case TargetState.Create:
                lines.Add($"未发现任何规则文件，可以安全生成 {TargetFileName}。");
                break;

            case TargetState.Exists:
                // 这句文案是对外承诺，逐字保留：它解释了「为什么没生成」，
                // 也给出唯一出路——否则用户只会以为 /init 坏了。
                lines.Add($"{TargetFileName} 已存在（{plan.Target}），未覆盖。想重新生成请先移走或删除它。");
                if (Directory.Exists(plan.Target))
                    lines.Add($"注意：{plan.Target} 是个目录，不是文件。先确认里面有什么，别直接删。");
                else
                    lines.Add("草稿只是起点，你的判断才是这份文件的价值——要合并就往现有文件里手工补，不要靠重新生成。");
                break;

            case TargetState.Shadow:
                {
                    var names = string.Join("、", plan.Existing);
                    var order = string.Join(" > ", plan.Candidates);
                    lines.Add($"未生成 {TargetFileName}：新建的 {TargetFileName} 会把 {names} 整个盖住，你现有的规则一个字都读不到。");
                    lines.Add($"规则文件按优先级只取第一个存在的（{order}），所以 {names} 现在就在生效，不需要改名。");
                    lines.Add($"确实想换成 {TargetFileName}：先把 {names} 的内容复制进新文件，再删除旧文件，最后重跑 /init；保留 {names} 也完全没问题。");
                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(plan), plan.State, "未知的目标状态");
        }
        return lines;
    }

    /// <summary>扫描 <paramref name="root"/> 并生成草稿。纯逻辑：不写文件、不抛异常。</summary>
    public static Result Generate(string root)
    {
        var findings = new List<string>();
        var sb = new StringBuilder();
        sb.AppendLine("# 项目规则");
        sb.AppendLine();
        sb.AppendLine("<!-- 由 `codeagent /init` 生成。请按实际需要增删： -->");
        sb.AppendLine("<!-- 本文件的内容会在每次会话开始时注入系统提示。 -->");
        sb.AppendLine();

        // —— 项目概览 ——
        var projectName = Path.GetFileName(
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        sb.AppendLine("## 项目");
        sb.AppendLine();
        sb.AppendLine($"- 名称: {projectName}");

        var stack = DetectStack(root, findings);
        if (stack.Count > 0)
        {
            sb.AppendLine($"- 技术栈: {string.Join(" / ", stack)}");
            findings.Add($"技术栈: {string.Join(" / ", stack)}");
        }

        var layout = DetectLayout(root, findings);
        if (layout.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### 目录");
            sb.AppendLine();
            foreach (var (dir, what) in layout)
                sb.AppendLine($"- `{dir}/` — {what}");
        }

        // —— 构建与测试 ——
        var commands = DetectCommands(root, stack, findings);
        if (commands.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 构建与测试");
            sb.AppendLine();
            sb.AppendLine("提交前请确保这些命令通过：");
            sb.AppendLine();
            sb.AppendLine("```bash");
            foreach (var cmd in commands)
                sb.AppendLine(cmd);
            sb.AppendLine("```");
        }

        // —— 约定（这部分给的是**可执行的规矩**，不是口号） ——
        sb.AppendLine();
        sb.AppendLine("## 约定");
        sb.AppendLine();
        sb.AppendLine("- 改动尽量小、聚焦当前任务，不要顺手重构无关代码。");
        sb.AppendLine("- 新增行为请补测试；已有测试不要为了让改动过而放宽断言。");
        if (HasFile(root, "AGENTS.md"))
            sb.AppendLine("- 本文件即项目规则，改动后立即生效（无需重启）。");

        return new Result(sb.ToString().TrimEnd() + "\n", findings);
    }

    private static bool HasFile(string root, string name) =>
        File.Exists(Path.Combine(root, name));

    private static bool AnyFile(string root, params string[] names) =>
        names.Any(n => File.Exists(Path.Combine(root, n)));

    /// <summary>按根目录里的标志文件判断技术栈。只认证据，不猜。</summary>
    private static List<string> DetectStack(string root, List<string> findings)
    {
        var stack = new List<string>();
        if (Directory.EnumerateFiles(root, "*.csproj").Any()
            || Directory.EnumerateFiles(root, "*.sln").Any()
            || Directory.EnumerateFiles(root, "*.slnx").Any())
            stack.Add(".NET");
        if (AnyFile(root, "package.json"))
            stack.Add("Node.js");
        if (AnyFile(root, "pyproject.toml", "requirements.txt", "setup.py"))
            stack.Add("Python");
        if (AnyFile(root, "Cargo.toml"))
            stack.Add("Rust");
        if (AnyFile(root, "go.mod"))
            stack.Add("Go");
        if (AnyFile(root, "pom.xml", "build.gradle", "build.gradle.kts"))
            stack.Add("JVM");
        if (AnyFile(root, "CMakeLists.txt"))
            stack.Add("CMake");
        return stack;
    }

    /// <summary>只列**存在的**常见目录，附一句用途。没有的不编。</summary>
    private static List<(string Dir, string What)> DetectLayout(string root, List<string> findings)
    {
        (string Dir, string What)[] candidates =
        [
            ("src", "源码"),
            ("tests", "测试"),
            ("test", "测试"),
            ("docs", "文档"),
            ("scripts", "脚本"),
            ("tools", "工具"),
            ("examples", "示例"),
        ];
        var found = new List<(string, string)>();
        foreach (var (dir, what) in candidates)
        {
            if (Directory.Exists(Path.Combine(root, dir)))
                found.Add((dir, what));
        }
        return found;
    }

    /// <summary>
    /// 按技术栈给出**可直接执行**的构建/测试命令。
    ///
    /// 每个生态只在对应标志文件存在时才给命令：
    /// 给错命令比不给更糟——用户会照着跑，失败后怀疑整个工具。
    /// </summary>
    private static List<string> DetectCommands(string root, List<string> stack, List<string> findings)
    {
        var cmds = new List<string>();
        if (stack.Contains(".NET"))
        {
            cmds.Add("dotnet build");
            // 有测试工程才给 test：没有测试的仓库里 `dotnet test` 会报「找不到测试」
            if (Directory.EnumerateDirectories(root, "*Test*", SearchOption.AllDirectories)
                    .Any(d => Directory.EnumerateFiles(d, "*.csproj").Any())
                || Directory.EnumerateDirectories(root, "*Tests*", SearchOption.AllDirectories).Any())
                cmds.Add("dotnet test");
        }
        if (stack.Contains("Node.js"))
        {
            cmds.Add("npm install");
            if (HasFile(root, "package-lock.json") || HasFile(root, "pnpm-lock.yaml") || HasFile(root, "yarn.lock"))
            {
                cmds.Add("npm test");
            }
        }
        if (stack.Contains("Python"))
        {
            cmds.Add("python -m pytest");
        }
        if (stack.Contains("Rust"))
        {
            cmds.Add("cargo build");
            cmds.Add("cargo test");
        }
        if (stack.Contains("Go"))
        {
            cmds.Add("go build ./...");
            cmds.Add("go test ./...");
        }
        return cmds;
    }
}
