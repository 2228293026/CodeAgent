using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class SkipDirsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-skips-" + Guid.NewGuid().ToString("N"));

    public SkipDirsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    [Fact]
    public void EnumerateFilesPruned_SkipsBuildAndCacheDirs()
    {
        // 布局：src/a.cs、src/deep/b.cs、node_modules/x.cs、bin/y.cs（都应被剪枝跳过）
        Directory.CreateDirectory(Path.Combine(_dir, "src", "deep"));
        Directory.CreateDirectory(Path.Combine(_dir, "node_modules"));
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "src", "a.cs"), "a");
        File.WriteAllText(Path.Combine(_dir, "src", "deep", "b.cs"), "b");
        File.WriteAllText(Path.Combine(_dir, "node_modules", "x.cs"), "x");
        File.WriteAllText(Path.Combine(_dir, "bin", "y.cs"), "y");

        var files = SkipDirs.EnumerateFilesPruned(_dir)
            .Select(f => Path.GetRelativePath(_dir, f).Replace('\\', '/'))
            .OrderBy(x => x)
            .ToList();

        Assert.Contains("src/a.cs", files);
        Assert.Contains("src/deep/b.cs", files);
        Assert.DoesNotContain(files, f => f.Contains("node_modules") || f.Contains("/bin/"));
    }

    [Fact]
    public void EnumerateFilesPruned_SkipsVenvAndTerraform()
    {
        // 回归：常见语言的缓存目录也应剪枝
        Directory.CreateDirectory(Path.Combine(_dir, ".venv", "lib"));
        Directory.CreateDirectory(Path.Combine(_dir, ".terraform", "modules"));
        Directory.CreateDirectory(Path.Combine(_dir, ".pytest_cache"));
        File.WriteAllText(Path.Combine(_dir, ".venv", "lib", "x.py"), "x");
        File.WriteAllText(Path.Combine(_dir, ".terraform", "modules", "m.tf"), "m");
        File.WriteAllText(Path.Combine(_dir, ".pytest_cache", "c.py"), "c");
        File.WriteAllText(Path.Combine(_dir, "real.py"), "r");

        var files = SkipDirs.EnumerateFilesPruned(_dir)
            .Select(f => Path.GetRelativePath(_dir, f).Replace('\\', '/'))
            .ToList();

        Assert.Contains("real.py", files);
        Assert.DoesNotContain(files, f => f.Contains(".venv") || f.Contains(".terraform") || f.Contains(".pytest_cache"));
    }

    [Fact]
    public void EnumerateFilesPruned_SkipsPackageAndFrontendCaches()
    {
        // 包管理器缓存与前端构建缓存：扫描不应进入（否则 node_modules 之外的缓存目录也被扫）
        foreach (var d in new[] { ".cache", ".npm", ".yarn", ".pnpm-store", ".turbo", ".eslintcache",
                                  ".parcel-cache", ".angular", ".svelte-kit", ".cargo", "vendor", ".bundle" })
            Directory.CreateDirectory(Path.Combine(_dir, d));
        File.WriteAllText(Path.Combine(_dir, ".npm", "x.js"), "x");
        File.WriteAllText(Path.Combine(_dir, ".turbo", "t.js"), "t");
        File.WriteAllText(Path.Combine(_dir, "vendor", "v.php"), "v");
        File.WriteAllText(Path.Combine(_dir, "app.js"), "ok");

        var files = SkipDirs.EnumerateFilesPruned(_dir)
            .Select(f => Path.GetRelativePath(_dir, f).Replace('\\', '/'))
            .ToList();

        Assert.Contains("app.js", files);
        Assert.DoesNotContain(files, f => f.Contains(".npm") || f.Contains(".turbo") || f.Contains("vendor"));
    }

    [Fact]
    public void EnumerateFilesPruned_SkipsVendoredLibsAndTestArtifacts()
    {
        // libs（mod/游戏项目引用 DLL）、third_party/external（vendored 源码）、
        // TestResults / artifacts / coverage（测试与构建产物）
        foreach (var d in new[] { "libs", "third_party", "external", "TestResults", "artifacts", "coverage" })
            Directory.CreateDirectory(Path.Combine(_dir, d));
        File.WriteAllText(Path.Combine(_dir, "libs", "Assembly-CSharp.dll"), "b");
        File.WriteAllText(Path.Combine(_dir, "third_party", "t.cpp"), "t");
        File.WriteAllText(Path.Combine(_dir, "TestResults", "run.trx"), "r");
        File.WriteAllText(Path.Combine(_dir, "src.cs"), "ok");

        var files = SkipDirs.EnumerateFilesPruned(_dir)
            .Select(f => Path.GetRelativePath(_dir, f).Replace('\\', '/'))
            .ToList();

        Assert.Contains("src.cs", files);
        Assert.DoesNotContain(files, f => f.Contains("libs/") || f.Contains("third_party") ||
                                          f.Contains("TestResults") || f.Contains("artifacts") || f.Contains("coverage"));
    }

    [Fact]
    public void ComputeFileSha256_MatchesKnownVector()
    {
        // 流式哈希结果必须与标准向量一致（"abc" 的 SHA256）
        var path = Path.Combine(_dir, "abc.txt");
        File.WriteAllText(path, "abc");
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
            SkipDirs.ComputeFileSha256(path));
    }

    [Fact]
    public void ComputeFileSha256_MissingFile_ReturnsNull()
    {
        // 读取失败返回 null（调用方跳过显示），不抛异常
        Assert.Null(SkipDirs.ComputeFileSha256(Path.Combine(_dir, "nope.txt")));
    }

    [Fact]
    public void CountFileLines_CountsLikeReadAllLines()
    {
        // 流式行数须与 File.ReadAllLines 语义一致：末尾无换行也算一行
        var withTrailing = Path.Combine(_dir, "trailing.txt");
        File.WriteAllText(withTrailing, "a\nb\nc\n");
        Assert.Equal(3, SkipDirs.CountFileLines(withTrailing));

        var noTrailing = Path.Combine(_dir, "notrailing.txt");
        File.WriteAllText(noTrailing, "a\nb\nc");
        Assert.Equal(3, SkipDirs.CountFileLines(noTrailing));
    }

    [Fact]
    public void CountFileLines_EmptyFile_ReturnsZero()
    {
        var empty = Path.Combine(_dir, "empty.txt");
        File.WriteAllText(empty, "");
        Assert.Equal(0, SkipDirs.CountFileLines(empty));
    }

    [Fact]
    public void CountFileLines_LargeFile_StreamsCorrectly()
    {
        // 跨 64KB 缓冲区边界：确保分块读取不丢行、不重复计数
        var big = Path.Combine(_dir, "big.txt");
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 20000; i++)
            sb.Append("line ").Append(i).Append('\n');
        File.WriteAllText(big, sb.ToString());
        Assert.Equal(20000, SkipDirs.CountFileLines(big));
    }

    [Fact]
    public void TempPathFor_NormalPath_KeepsTempFileInSameDirectory()
    {
        // 临时文件必须与目标同目录（同卷 rename 才原子）
        var target = Path.Combine(_dir, "sub", "out.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = SkipDirs.TempPathFor(target);

        Assert.Equal(Path.GetDirectoryName(target), Path.GetDirectoryName(tmp));
        Assert.NotEqual(target, tmp); // 不能就是目标本身
        Assert.StartsWith(".", Path.GetFileName(tmp));
        Assert.EndsWith(".tmp", tmp);
    }

    [Fact]
    public void TempPathFor_VolumeRoot_DoesNotThrow()
    {
        // 防御性契约：卷根下 GetDirectoryName 返回 null，helper 必须兜底而不是让 Path.Combine 抛异常。
        // 注：write_file 的目录检查（Directory.Exists → ToolException）会先拦下卷根，
        // 故这不是用户可达崩溃；此测试锁定 helper 自身契约，避免后续复用者踩坑。
        var root = Path.GetPathRoot(Path.GetTempPath());
        Assert.NotNull(root);
        Assert.Null(Path.GetDirectoryName(root)); // 前提：该根确实取不到父目录

        var tmp = SkipDirs.TempPathFor(root); // 不应抛异常

        Assert.False(string.IsNullOrWhiteSpace(tmp));
        Assert.EndsWith(".tmp", tmp);
    }

    [Fact]
    public void TempPathFor_RepeatedCalls_ProduceUniquePaths()
    {
        // 并发写同一目标时临时文件不能互相覆盖
        var target = Path.Combine(_dir, "same.txt");
        var a = SkipDirs.TempPathFor(target);
        var b = SkipDirs.TempPathFor(target);

        Assert.NotEqual(a, b);
    }
}
