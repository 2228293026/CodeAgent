namespace CodeAgent;

/// <summary>轻量 git 信息读取：直接解析 .git/HEAD，不启动 git 进程（状态栏每轮提示符都刷新，
/// 进程派生的开销与失败模式不可接受）。不依赖 git 安装；裸目录返回 null。</summary>
public static class GitInfo
{
    /// <summary>读取 dir（或其 worktree gitdir）当前分支名。规则：
    /// - .git 为目录 → 读其中 HEAD；
    /// - .git 为文件（worktree/submodule）→ 按 "gitdir: &lt;path&gt;" 解析真实 gitdir 再读 HEAD；
    /// - HEAD 是符号引用 → refs/heads/ 后的分支名；
    /// - detached HEAD → "detached:短哈希"；
    /// 任何失败（非仓库/无权限/损坏）返回 null。</summary>
    public static string? CurrentBranch(string dir)
    {
        try
        {
            var gitPath = Path.Combine(dir, ".git");
            string gitDir;
            if (Directory.Exists(gitPath))
                gitDir = gitPath;
            else if (File.Exists(gitPath))
            {
                // worktree/submodule：.git 是指向真实 gitdir 的指针文件
                var line = File.ReadAllText(gitPath).Trim();
                const string prefix = "gitdir:";
                if (!line.StartsWith(prefix, StringComparison.Ordinal))
                    return null;
                var p = line[prefix.Length..].Trim();
                if (p.Length == 0)
                    return null;
                gitDir = Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(dir, p));
            }
            else
                return null; // 非 git 仓库

            var head = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(head))
                return null;
            var content = File.ReadAllText(head).Trim();
            const string refPrefix = "ref:";
            if (content.StartsWith(refPrefix, StringComparison.Ordinal))
            {
                var refName = content[refPrefix.Length..].Trim();
                const string headsPrefix = "refs/heads/";
                var branch = refName.StartsWith(headsPrefix, StringComparison.Ordinal)
                    ? refName[headsPrefix.Length..]
                    : refName;
                return branch.Length == 0 ? null : branch;
            }
            // detached HEAD：内容是裸 SHA，短哈希足以在状态栏辨识
            return content.Length >= 7 ? "detached:" + content[..7] : null;
        }
        catch
        {
            return null;
        }
    }
}
