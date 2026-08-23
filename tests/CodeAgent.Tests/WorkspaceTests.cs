using System;
using System.IO;
using System.Linq;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public class WorkspaceTests
{
    // 跨平台：用临时目录代替硬编码的 Windows 路径（CI 在 Linux 上跑）
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "codeagent-ws-test");

    [Fact]
    public void Resolve_NullOrEmpty_ReturnsRoot()
    {
        var ws = new Workspace(Root);
        Assert.Equal(Path.GetFullPath(Root), ws.Resolve(null));
        Assert.Equal(Path.GetFullPath(Root), ws.Resolve(""));
    }

    [Fact]
    public void Resolve_InsideRoot_ReturnsFullPath()
    {
        var ws = new Workspace(Root);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Root, "src", "a.cs")),
            ws.Resolve(Path.Combine("src", "a.cs")));
    }

    [Fact]
    public void Resolve_Dot_ReturnsRoot()
    {
        var ws = new Workspace(Root);
        Assert.Equal(Path.GetFullPath(Root), ws.Resolve("."));
    }

    [Fact]
    public void Resolve_TrailingSeparator_OnFilePath_IsNormalized()
    {
        // 回归：模型常写 "a.txt/" —— GetFullPath 保留尾分隔符会让
        // File.Exists / Directory.Exists 双双失配，read_file 误报「文件不存在」
        var ws = new Workspace(Root);
        var expected = Path.GetFullPath(Path.Combine(Root, "a.txt"));
        Assert.Equal(expected, ws.Resolve("a.txt/"));
        Assert.Equal(expected, ws.ResolveRead("./a.txt\\"));
        // 工作区根本身不受影响
        Assert.Equal(Path.GetFullPath(Root), ws.Resolve("."));
    }

    [Fact]
    public void Resolve_Escape_Throws()
    {
        var ws = new Workspace(Root);
        Assert.Throws<ToolException>(() => ws.Resolve(Path.Combine("..", "secret.txt")));
    }

    [Fact]
    public void Resolve_SiblingPrefix_Rejected()
    {
        // 同级前缀目录（如 proj2）不应被 proj 的沙箱放行
        var ws = new Workspace(Root);
        Assert.Throws<ToolException>(() => ws.Resolve(Path.Combine("..", "proj2", "x.txt")));
    }

    [Fact]
    public void Resolve_CaseVariant_MatchesFileSystemSemantics()
    {
        // 回归：Windows 大小写不敏感（../PROJ 应放行，等同根目录）；
        // Linux/macOS 大小写敏感（../PROJ 是另一目录，必须拒绝），否则沙箱可被大小写变体绕过。
        var ws = new Workspace(Root);
        var caseVariant = Path.Combine("..", Root.Split(Path.DirectorySeparatorChar).Last().ToUpperInvariant());
        if (OperatingSystem.IsWindows())
        {
            // Windows 大小写不敏感：放行（Path.GetFullPath 会保留传入的大小写，故只断言不抛异常）
            _ = ws.Resolve(caseVariant);
        }
        else
        {
            Assert.Throws<ToolException>(() => ws.Resolve(caseVariant));
        }
    }

    [Fact]
    public void Resolve_AbsolutePathOutside_IsRejected()
    {
        // 绝对路径（如 C:\Windows 或 /etc）即使以合法相对形式传入也应被拒绝
        var ws = new Workspace(Root);
        var absolute = Path.Combine(Path.GetTempPath(), "codeagent-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(absolute);
        try
        {
            Assert.Throws<ToolException>(() => ws.Resolve(absolute));
        }
        finally
        {
            try { Directory.Delete(absolute, true); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void Resolve_InvalidPathChars_ThrowsToolException()
    {
        // 回归：含 NUL 等非法字符的路径曾让 Path.GetFullPath 抛裸 ArgumentException，
        // 应转为清晰的 ToolException
        var ws = new Workspace(Root);
        var ex = Assert.Throws<ToolException>(() => ws.Resolve("bad\0name.txt"));
        Assert.Contains("路径非法", ex.Message);
    }

    [Fact]
    public void Resolve_SymlinkEscape_IsRejected()
    {
        // 回归：工作区内的符号链接指向外部时，应通过链接解析后的真实路径拦截越界
        var outside = Path.Combine(Path.GetTempPath(), "codeagent-link-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var wsRoot = Path.Combine(Path.GetTempPath(), "codeagent-link-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wsRoot);
        try
        {
            var link = Path.Combine(wsRoot, "escape");
            try
            {
                Directory.CreateSymbolicLink(link, outside); // 不支持的平台/无权限时跳过
            }
            catch
            {
                return; // 无符号链接权限（如 Windows 非开发者模式）：跳过
            }
            var ws = new Workspace(wsRoot);
            Assert.Throws<ToolException>(() => ws.Resolve(Path.Combine("escape", "secret.txt")));
        }
        finally
        {
            try { Directory.Delete(wsRoot, true); } catch { /* 忽略 */ }
            try { Directory.Delete(outside, true); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void Resolve_SymlinkEscape_ExistingFile_IsRejected()
    {
        // 回归：目标文件已存在时（如 read_file 经过 symlink 目录读一个真实存在的文件），
        // 旧实现只解析最深一段、对非链接叶子返回 null，中间层的链接被漏掉 → 沙箱被穿越。
        // 无符号链接权限的 Windows 上退回 junction（mklink /J 无需管理员，同为 reparse point，
        // ResolveLinkTarget 同样解析），保证本回归测试可在 CI 上真实执行
        var outside = Path.Combine(Path.GetTempPath(), "codeagent-link-out2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var wsRoot = Path.Combine(Path.GetTempPath(), "codeagent-link-ws3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wsRoot);
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
            var link = Path.Combine(wsRoot, "escape");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch
            {
                if (!OperatingSystem.IsWindows())
                    return; // 非 Windows 且符号链接不可用：跳过
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                p?.WaitForExit(5000);
                if (!Directory.Exists(link))
                    return; // junction 也建不了：跳过
            }
            var ws = new Workspace(wsRoot);
            Assert.Throws<ToolException>(() => ws.ResolveRead(Path.Combine("escape", "secret.txt")));
            Assert.Throws<ToolException>(() => ws.Resolve(Path.Combine("escape", "secret.txt")));
        }
        finally
        {
            try { Directory.Delete(wsRoot, true); } catch { /* 忽略 */ }
            try { Directory.Delete(outside, true); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void Resolve_SymlinkInside_IsAllowed()
    {
        // 符号链接指向工作区内时不应误拦
        var wsRoot = Path.Combine(Path.GetTempPath(), "codeagent-link-ws2-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(wsRoot, "real");
        Directory.CreateDirectory(target);
        try
        {
            var link = Path.Combine(wsRoot, "alias");
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch
            {
                return; // 无权限：跳过
            }
            var ws = new Workspace(wsRoot);
            _ = ws.Resolve(Path.Combine("alias", "x.txt")); // 不应抛异常
        }
        finally
        {
            try { Directory.Delete(wsRoot, true); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void ToRelative_ConvertsBack()
    {
        var ws = new Workspace(Root);
        Assert.Equal(
            Path.Combine("src", "a.cs"),
            ws.ToRelative(Path.GetFullPath(Path.Combine(Root, "src", "a.cs"))));
    }

    [Fact]
    public void ToRelative_RootItself_ReturnsEmpty()
    {
        var ws = new Workspace(Root);
        Assert.Equal("", ws.ToRelative(Path.GetFullPath(Root)));
    }

    [Fact]
    public void ToRelative_TrailingSeparator_IsNormalized()
    {
        // 结尾带分隔符的路径也应转成不带分隔符的相对路径
        var ws = new Workspace(Root);
        var dir = Path.GetFullPath(Path.Combine(Root, "src")) + Path.DirectorySeparatorChar;
        Assert.Equal("src", ws.ToRelative(dir));
    }

    [Fact]
    public void ToRelative_CrossDrive_ReturnsFullPath()
    {
        // 回归：full 模式扫描工作区外文件时，跨盘符路径曾让
        // Path.GetRelativePath 抛 ArgumentException 直接崩溃
        if (!OperatingSystem.IsWindows())
            return; // Linux/macOS 单一根，不存在跨盘符场景
        var ws = new Workspace(Root);
        var tempRoot = Path.GetPathRoot(Path.GetFullPath(Root))!;
        var otherDrive = tempRoot.TrimEnd('\\', '/').Equals("C:", StringComparison.OrdinalIgnoreCase)
            ? @"Z:\outside\file.txt"
            : @"C:\outside\file.txt";
        Assert.Equal(otherDrive, ws.ToRelative(otherDrive));
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("dir/b.txt")]
    [InlineData("a/b/c/d.txt")]
    public void Resolve_DeepRelativePaths_StayInside(string rel)
    {
        var ws = new Workspace(Root);
        var full = ws.Resolve(rel);
        Assert.Equal(Path.GetFullPath(Path.Combine(Root, rel)), full);
    }

    [Theory]
    [InlineData("../x.txt")]
    [InlineData("../../x.txt")]
    [InlineData("a/../../x.txt")]      // 中间穿越
    [InlineData("a/../../../x.txt")]
    public void Resolve_EscapeVariants_AllRejected(string rel)
    {
        var ws = new Workspace(Root);
        Assert.Throws<ToolException>(() => ws.Resolve(rel));
    }

    [Fact]
    public void Resolve_WorkspaceRootViaParent_NormalizesInside()
    {
        // ../<根目录名>/x.txt 经 Path.GetFullPath 规范化后落在工作区内，应放行而非误拦
        var ws = new Workspace(Root);
        var parentName = new DirectoryInfo(Root).Name;
        var full = ws.Resolve(Path.Combine("..", parentName, "x.txt"));
        Assert.Equal(Path.GetFullPath(Path.Combine(Root, "x.txt")), full);
    }

    // ===== 只读白名单（readOnlyDirs）=====

    private static readonly string Outside = Path.Combine(Path.GetTempPath(), "codeagent-ws-outside");

    [Fact]
    public void ResolveRead_ReadOnlyDir_AllowsRead()
    {
        // whitelist 模式：白名单目录（如兄弟项目 adofai-libs）内的路径，读工具（ResolveRead）应放行
        var ws = new Workspace(Root, new[] { Outside }, "whitelist");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Outside, "AdofaiKnowledge.md")),
            ws.ResolveRead(Path.Combine("..", Path.GetFileName(Outside), "AdofaiKnowledge.md")));
    }

    [Fact]
    public void Resolve_ReadOnlyDir_StillRejectsWrite()
    {
        // whitelist 模式：同一白名单目录，写工具（Resolve）必须拒绝——白名单只读，不可写
        var ws = new Workspace(Root, new[] { Outside }, "whitelist");
        Assert.Throws<ToolException>(() =>
            ws.Resolve(Path.Combine("..", Path.GetFileName(Outside), "mod.cs")));
    }

    [Fact]
    public void ResolveRead_OutsideWhitelist_StillRejected()
    {
        // whitelist 模式：白名单之外的目录（未配置的兄弟项目）读工具也要拒绝，不能整体放开
        var ws = new Workspace(Root, new[] { Outside }, "whitelist");
        var other = Path.Combine(Path.GetTempPath(), "codeagent-ws-other-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<ToolException>(() =>
            ws.ResolveRead(Path.Combine("..", Path.GetFileName(other), "x.txt")));
    }

    [Fact]
    public void ResolveRead_RelativeReadOnlyDir_ResolvesFromRoot()
    {
        // whitelist 模式：相对路径白名单（如 ../adofai-libs）按工作区解析；其内部读放行、写拒绝。
        // 注意：落在工作区内的相对路径（如 "ext"）本就在工作区内，Resolve 放行是正确的，不算白名单场景
        var ws = new Workspace(Root, new[] { Path.Combine("..", "ext") }, "whitelist");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Root, "..", "ext", "kb.md")),
            ws.ResolveRead(Path.Combine("..", "ext", "kb.md")));
        Assert.Throws<ToolException>(() => ws.Resolve(Path.Combine("..", "ext", "kb.md")));
    }

    [Fact]
    public void Strict_IgnoresReadOnlyDirs()
    {
        // strict 模式（默认）：即使配置了白名单，读工具也不放行白名单目录——严格模式就是纯工作区沙箱
        var ws = new Workspace(Root, new[] { Outside });
        Assert.Throws<ToolException>(() =>
            ws.ResolveRead(Path.Combine("..", Path.GetFileName(Outside), "kb.md")));
    }

    // ===== 权限模式（fileAccess: strict / whitelist / full）=====

    [Fact]
    public void FullAccess_ReadWriteOutside_AllAllowed()
    {
        // full 模式：工作区之外的路径，读（ResolveRead）与写（Resolve）都放行
        var ws = new Workspace(Root, null, "full");
        var outside = Path.Combine(Path.GetTempPath(), "codeagent-full-" + Guid.NewGuid().ToString("N"), "x.cs");
        Assert.Equal(
            Path.GetFullPath(outside),
            ws.Resolve(Path.Combine("..", Path.GetTempPath(), outside))); // 路径以 .. 开头到工作区外
        Assert.Equal(
            Path.GetFullPath(outside),
            ws.ResolveRead(Path.Combine("..", Path.GetTempPath(), outside)));
    }

    [Fact]
    public void FullAccess_AbsolutePath_Allowed()
    {
        // full 模式：绝对路径（如 C:\Windows）不再被沙箱拦截
        var ws = new Workspace(Root, null, "full");
        var absolute = Path.Combine(Path.GetTempPath(), "codeagent-full-abs-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(Path.GetFullPath(absolute), ws.Resolve(absolute));
    }

    [Fact]
    public void Strict_Default_RejectsOutside()
    {
        // 默认 strict：未配置白名单时工作区外读写都拒绝（回归默认安全）
        var ws = new Workspace(Root);
        Assert.Throws<ToolException>(() => ws.Resolve(Path.Combine("..", "secret.txt")));
        Assert.Throws<ToolException>(() => ws.ResolveRead(Path.Combine("..", "secret.txt")));
    }

    [Fact]
    public void FullAccess_FlagExposed()
    {
        Assert.False(new Workspace(Root).FullAccess);
        Assert.False(new Workspace(Root, null, "whitelist").FullAccess);
        Assert.True(new Workspace(Root, null, "full").FullAccess);
        Assert.True(new Workspace(Root, null, "FULL").FullAccess); // 大小写不敏感
    }

    [Fact]
    public void SetFileAccess_SwitchesAtRuntime()
    {
        // Shift+Tab / /access 运行时切换：strict → full 后工作区外路径放行，切回 strict 恢复拒绝
        var ws = new Workspace(Root);
        Assert.Throws<ToolException>(() => ws.Resolve(Path.Combine("..", "x.txt")));

        ws.SetFileAccess("full");
        Assert.True(ws.FullAccess);
        var outside = Path.Combine(Path.GetTempPath(), "codeagent-runtime-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(Path.GetFullPath(outside), ws.Resolve(outside)); // 绝对路径放行

        ws.SetFileAccess("strict");
        Assert.False(ws.FullAccess);
        Assert.Throws<ToolException>(() => ws.Resolve(outside));
    }

    [Fact]
    public void SetFileAccess_WhitelistAtRuntime_ActivatesReadOnlyDirs()
    {
        // 运行时切到 whitelist（/access whitelist）：配置过的只读白名单目录读放行、写仍拦截
        var ws = new Workspace(Root, new[] { Outside }, "strict");
        var kb = Path.Combine(Outside, "kb.md");
        Assert.Throws<ToolException>(() => ws.ResolveRead(kb)); // strict 下白名单不生效

        ws.SetFileAccess("whitelist");
        Assert.False(ws.FullAccess);
        Assert.Equal(Path.GetFullPath(kb), ws.ResolveRead(kb));
        Assert.Throws<ToolException>(() => ws.Resolve(kb)); // 白名单目录只读不可写

        ws.SetFileAccess("strict");
        Assert.Throws<ToolException>(() => ws.ResolveRead(kb)); // 切回 strict：白名单重新关闭
    }

    [Fact]
    public void ReadOnlyRoots_ExposesNormalizedDirs()
    {
        // ReadOnlyRoots 暴露规范化后的白名单目录（去尾分隔符），供诊断/确认显示
        var ws = new Workspace(Root, new[] { Outside + Path.DirectorySeparatorChar, " ", null! });
        var roots = ws.ReadOnlyRoots;
        Assert.Single(roots);
        Assert.Equal(Path.GetFullPath(Outside).TrimEnd(Path.DirectorySeparatorChar), roots[0]);
    }
}
