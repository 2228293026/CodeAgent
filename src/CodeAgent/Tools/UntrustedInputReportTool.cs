using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查数据边界的漏洞：接口直接接受来自外部的路径/URL 就打开、
/// 把用户输入拼进 SQL/命令、以及把敏感字段整个序列化出去。</summary>
public sealed class UntrustedInputReportTool : ITool
{
    public string Name => "untrusted_input_report";
    public string Description =>
        "只读检查不可信输入的使用：用户输入直接拼进路径/SQL/命令、反射加载外部程序集、以及把内部结构整个序列化返回。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示每类问题数（默认 30，最大 300）" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "扫描深度上限（默认 10，最大 32）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var shellInjection = new List<string>();
        var rawPathUse = new List<string>();
        var dynamicLoad = new List<string>();
        var files = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;
            files++;
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            // 不可信参数的判定：来自命令行、请求、输入流、配置、工具参数
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                // 把变量拼进命令行。必须看**整行**的插值串，而不是"紧跟 API 的第一个参数"——
                // `Process.Start("cmd", $"/c dir {x}")` 的第一个参数是程序名，
                // 只盯第一个就整类漏掉最典型的写法。
                if (Regex.IsMatch(trimmed, @"(?:Process\.Start|new\s+ProcessStartInfo|\bArguments\s*=|\bFileName\s*=)"))
                {
                    foreach (Match lit in Regex.Matches(trimmed, @"\$""[^""]*"""))
                    {
                        foreach (Match iv in Regex.Matches(lit.Value, @"\{(?<v>\w+)\}"))
                        {
                            if (IsUntrusted(iv.Groups["v"].Value))
                                shellInjection.Add($"{relative}:{lineNo} 把 {iv.Groups["v"].Value}（外部输入）拼进命令行——其中的引号/分号会被 shell 解释");
                        }
                    }
                }
                // SQL 拼接。关键字在**插值串内部**（`$"SELECT … {v}"`）——
                // 早先要求 SELECT 出现在 `$"` **之前**，而真实代码顺序正好相反，
                // 于是这类（最常见的）写法整类漏掉。
                foreach (Match lit in Regex.Matches(trimmed, @"\$""[^""]*"""))
                {
                    var litText = lit.Value;
                    if (!Regex.IsMatch(litText, @"(?i)\b(SELECT|INSERT\s+INTO|UPDATE|DELETE\s+FROM)\b"))
                        continue;
                    foreach (Match iv in Regex.Matches(litText, @"\{(?<v>\w+)\}"))
                    {
                        if (IsUntrusted(iv.Groups["v"].Value))
                            rawPathUse.Add($"{relative}:{lineNo} SQL 字符串里直接插值 {iv.Groups["v"].Value}（外部输入）——应参数化");
                    }
                }
                // 外部路径直接打开。既认插值 `File.ReadAllText($"{dir}/a")`，
                // 也认**裸参数** `File.ReadAllText(path)`——后者才是最常见的写法，
                // 只认插值等于把最典型的形态整类漏掉。
                var open = Regex.Match(trimmed, @"(?:File\.(?:Open|ReadAllText|ReadAllLines|ReadAllBytes|WriteAllText|WriteAllBytes|Delete|Exists|Copy)|Directory\.(?:GetFiles|EnumerateFiles|Delete|CreateDirectory)|new\s+FileStream)\s*\(\s*\$?""[^\n]*\{(?<v>\w+)\}");
                if (open.Success && IsUntrusted(open.Groups["v"].Value))
                    rawPathUse.Add($"{relative}:{lineNo} 直接用 {open.Groups["v"].Value}（外部输入）打开路径——未做根目录校验");
                var bare = Regex.Match(trimmed, @"(?:File\.(?:Open|ReadAllText|ReadAllLines|ReadAllBytes|WriteAllText|WriteAllBytes|Delete|Exists|Copy)|Directory\.(?:GetFiles|EnumerateFiles|Delete|CreateDirectory))\s*\(\s*(?<v>[A-Za-z_]\w*)\s*[,)]");
                if (bare.Success && IsUntrusted(bare.Groups["v"].Value)
                    && !trimmed.Contains("Path.GetFullPath", StringComparison.Ordinal)
                    && !trimmed.Contains("IsPathWithin", StringComparison.Ordinal))
                    rawPathUse.Add($"{relative}:{lineNo} 直接把 {bare.Groups["v"].Value}（外部输入）交给文件 API——未做根目录校验");
                // 动态加载程序集
                if (Regex.IsMatch(trimmed, @"Assembly\.Load(?:From|File)?\s*\(") && Regex.IsMatch(trimmed, @"\{(?<v>\w+)\}"))
                    dynamicLoad.Add($"{relative}:{lineNo} 从外部输入动态加载程序集（等于执行任意代码）");
            }
        }
        if (files == 0)
            return "不可信输入报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"不可信输入报告: {files} 个 .cs 文件");
        Report(output, "命令/SQL 拼接", shellInjection, maxResults);
        Report(output, "未校验的外部输入使用", rawPathUse, maxResults);
        Report(output, "动态加载外部程序集", dynamicLoad, maxResults);
        if (shellInjection.Count == 0 && rawPathUse.Count == 0 && dynamicLoad.Count == 0)
            output.AppendLine("未发现不可信输入问题");
        return output.ToString().TrimEnd();
    }

    /// <summary>变量名是否代表**外部输入**。只认明确来源，不猜——
    /// 猜宽了会把所有本地变量都报一遍，报告立刻没人看。
    /// 但"不猜"不等于漏掉常见来源：<c>userId</c>/<c>account</c>/<c>session</c>
    /// 这些前缀和 SQL/命令上下文里出现时，几乎必然来自外部。</summary>
    internal static bool IsUntrusted(string name) =>
        Regex.IsMatch(name, @"^(?:args|argv|input|stdin|request|req|query|param|user|username|account|session|userInput|path|filePath|url|uri|payload|body|raw|cmd|command|dir|filename|name|target|@)", RegexOptions.IgnoreCase)
        || name.EndsWith("Input", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Path", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Url", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Command", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Args", StringComparison.OrdinalIgnoreCase);

    private static void Report(StringBuilder output, string title, List<string> items, int maxResults)
    {
        if (items.Count == 0)
            return;
        output.AppendLine($"{title}: {items.Count}");
        foreach (var item in items.OrderBy(x => x, StringComparer.Ordinal).Take(maxResults))
            output.AppendLine($"  {item}");
        if (items.Count > maxResults)
            output.AppendLine($"  …（另有 {items.Count - maxResults} 处未显示）");
    }
}
