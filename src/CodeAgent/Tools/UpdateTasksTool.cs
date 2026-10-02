using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>任务状态。取值与 Claude Code 的 TodoWrite 一致，便于模型直接沿用习惯。</summary>
public enum TaskStatus
{
    Pending,
    InProgress,
    Completed,
}

/// <summary>单条任务。</summary>
/// <param name="Content">任务描述（模型给的原文，按显示宽度截断时才有损）。</param>
/// <param name="Status">当前状态。</param>
public sealed record AgentTask(string Content, TaskStatus Status);

/// <summary>
/// 会话内的任务清单（学自 Claude Code 的 TodoWrite 面板）。
///
/// 模型通过 <c>update_tasks</c> 工具改写它，REPL 侧随时用 <c>/tasks</c> 查看、
/// 回合结束时打印一行进度。清单是**给用户看的**——长任务里模型自己在做什么，
/// 不说出来就等于让用户盯着一个不动的 spinner 猜。
///
/// 刻意不做的事：
/// · 不做持久化。清单是**本轮工作状态**，跨会话恢复一份过期的任务表只会误导。
/// · 不做依赖关系。模型给的顺序就是执行顺序，模型没声明的依赖不该由我们去猜。
/// </summary>
public sealed class TaskList
{
    /// <summary>上限。模型偶尔会一次给几十条；超过这个数说明它把清单当文档写了。</summary>
    public const int MaxTasks = 50;

    private readonly List<AgentTask> _tasks = [];

    public IReadOnlyList<AgentTask> Tasks => _tasks;

    public int Count => _tasks.Count;

    public int CompletedCount => _tasks.Count(t => t.Status == TaskStatus.Completed);

    public int InProgressCount => _tasks.Count(t => t.Status == TaskStatus.InProgress);

    public bool IsEmpty => _tasks.Count == 0;

    public void Clear() => _tasks.Clear();

    /// <summary>整表替换。空表合法——模型做完所有事会把清单清空。</summary>
    ///
    /// 超过 <see cref="MaxTasks"/> 的部分会被丢弃，**必须让调用方知道丢了多少**：
    /// 任务面板是用户唯一能看见"模型正在做什么"的地方，
    /// 而回执里的「N 项」是**保留后**的数——模型提交 60 项、清单只剩 50 项时，
    /// 回执说的是「50 项」，用户与模型都会以为全部记下了。
    /// 被丢掉的那 10 项恰恰可能是模型排在后面的、还没开始做的活。
    /// </summary>
    public int Replace(IEnumerable<AgentTask> tasks)
    {
        _tasks.Clear();
        var dropped = 0;
        foreach (var t in tasks)
        {
            if (_tasks.Count >= MaxTasks)
            {
                dropped++;
                continue;
            }
            if (!string.IsNullOrWhiteSpace(t.Content))
                _tasks.Add(t with { Content = t.Content.Trim() });
        }
        LastDropped = dropped;
        return dropped;
    }

    /// <summary>最近一次 <see cref="Replace"/> 丢弃的条目数（超出上限的部分）。</summary>
    public int LastDropped { get; private set; }

    /// <summary>纯函数：把模型给的 JSON 解析成任务表。
    ///
    /// 单条解析失败**丢弃该条**而不是整表失败：模型少写一个字段不该让整份计划消失，
    /// 而计划全没了用户就完全看不到模型在干什么——那比少一条更糟。
    /// </summary>
    public static List<AgentTask> Parse(JsonNode? node)
    {
        var list = new List<AgentTask>();
        if (node is not JsonArray arr)
            return list;
        foreach (var item in arr)
        {
            if (item is not JsonObject o)
                continue;
            var content = o["content"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(content))
                continue;
            list.Add(new AgentTask(content.Trim(), ParseStatus(o["status"]?.GetValue<string>())));
        }
        return list;
    }

    /// <summary>状态名容错：认不出的按 <c>pending</c> 处理，**不猜成进行中**。
    /// 猜错方向会显示"正在做"一件模型其实没在做的事，那是在骗用户。</summary>
    internal static TaskStatus ParseStatus(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "completed" or "done" => TaskStatus.Completed,
        "in_progress" or "inprogress" or "doing" or "active" => TaskStatus.InProgress,
        _ => TaskStatus.Pending,
    };
}

/// <summary>
/// 让模型更新任务清单的工具（学自 Claude Code 的 TodoWrite）。
///
/// 刻意不注册为"可见工具"里的主力：它的返回值很短，因为清单会由 REPL 直接渲染——
/// 模型看到自己的清单被回显出来，才知道它写进去了。
/// </summary>
public sealed class UpdateTasksTool : ITool
{
    private TaskList? _tasks;

    public UpdateTasksTool() { }

    public void Attach(TaskList tasks) => _tasks = tasks;

    public string Name => "update_tasks";
    public string Description =>
        "更新本轮任务清单。每完成一步、或计划变化时调用一次，参数是完整的任务表（不是增量）。"
        + "状态取 pending / in_progress / completed。清单会展示给用户看，用于跟进长任务的进度。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["tasks"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "完整的任务表（每次调用替换整张表，不是追加）",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["content"] = new JsonObject { ["type"] = "string", ["description"] = "任务描述（一句话）" },
                        ["status"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("pending", "in_progress", "completed"),
                            ["description"] = "当前状态",
                        },
                    },
                    ["required"] = new JsonArray("content", "status"),
                },
            },
        },
        ["required"] = new JsonArray("tasks"),
    };

    public Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var parsed = TaskList.Parse(args?["tasks"]);
        if (_tasks is not null)
        {
            _tasks.Replace(parsed);
            return Task.FromResult(RenderAck(_tasks));
        }        // 没有挂清单时（如单次 CLI 调用）也要有可预期的回显，而不是静默成功
        return Task.FromResult(parsed.Count == 0
            ? "任务清单已清空。"
            : $"已记录 {parsed.Count} 条任务（{parsed.Count(t => t.Status == TaskStatus.Completed)} 项完成）。");
    }

    internal static string RenderAck(TaskList tasks)
    {
        if (tasks.IsEmpty)
            return "任务清单已清空。";
        // 丢弃的部分必须出现在回执里：模型刚提交的那份表里有多少根本没进来，
        // 它自己不知道，而它后面会按"我记下了 60 项"来安排工作。
        var dropped = tasks.LastDropped;
        var dropNote = dropped > 0
            ? $"，另有 {dropped} 项超出上限 {TaskList.MaxTasks} 未记录"
            : "";
        return $"任务清单已更新：{tasks.Count} 项（完成 {tasks.CompletedCount}，进行中 {tasks.InProgressCount}）{dropNote}。";
    }
}
