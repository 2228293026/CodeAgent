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
