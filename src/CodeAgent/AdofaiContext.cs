namespace CodeAgent;

/// <summary>
/// ADOFAI（A Dance of Fire and Ice）mod 项目自动适配。
/// 检测当前工作目录是否为 ADOFAI mod 项目（存在 Info.json 或引用游戏反编译程序集），
/// 若是则自动：① 把专属开发上下文追加到系统提示；② 注入 moddev / harmony / assetbundle 工作模式。
/// 配置文件（项目级 codeagent.json）只负责 provider 与默认模式，知识由本类统一提供。
/// </summary>
public static class AdofaiContext
{
    /// <summary>
    /// 判断目录是否为 ADOFAI mod 项目：
    /// 1. 根目录存在 Info.json 且含 AssemblyName + EntryMethod（mod 入口声明，UMM/MelonLoader 通用格式）；
    /// 2. 根目录或 libs/Libs 下存在 Assembly-CSharp.dll（游戏反编译程序集，adofai-libs 引用库）。
    /// </summary>
    public static bool Detect(string dir)
    {
        try
        {
            var info = Path.Combine(dir, "Info.json");
            if (File.Exists(info))
            {
                var text = File.ReadAllText(info);
                if (text.Contains("\"AssemblyName\"") && text.Contains("\"EntryMethod\""))
                    return true;
            }
            if (File.Exists(Path.Combine(dir, "Assembly-CSharp.dll")))
                return true;
            foreach (var libs in new[] { "libs", "Libs", "lib" })
            {
                if (Directory.Exists(Path.Combine(dir, libs)) &&
                    File.Exists(Path.Combine(dir, libs, "Assembly-CSharp.dll")))
                    return true;
            }
        }
        catch
        {
            // 检测失败按非 ADOFAI 项目处理，不影响正常启动
        }
        return false;
    }

    /// <summary>
    /// 查找 ADOFAI 反编译 API 知识库文件（AdofaiKnowledge.md）：
    /// 依次检查 当前目录 → 父目录 → 兄弟目录下的 adofai-libs/，找到即返回完整路径，否则 null。
    /// </summary>
    public static string? FindKnowledgeBase(string cwd)
    {
        try
        {
            var candidates = new List<string>();
            candidates.Add(Path.Combine(cwd, "AdofaiKnowledge.md"));
            var parent = Directory.GetParent(cwd)?.FullName;
            if (parent is not null)
            {
                candidates.Add(Path.Combine(parent, "AdofaiKnowledge.md"));
                candidates.Add(Path.Combine(parent, "adofai-libs", "AdofaiKnowledge.md"));
            }
            foreach (var p in candidates)
                if (File.Exists(p))
                    return p;
        }
        catch
        {
            // 查找失败按无知识库处理
        }
        return null;
    }

    /// <summary>追加到系统提示的 ADOFAI mod 开发专属上下文（仅在用户未自定义 systemPrompt 时注入）。</summary>
    public static string ExtraSystemPrompt { get; } = """
        【ADOFAI mod 开发上下文（自动检测注入）】
        这是 A Dance of Fire and Ice（ADOFAI）Unity 节奏游戏的 mod 项目。开发约定：
        - 工程形态：C# / .NET Framework 4.8.1 传统 csproj（非 SDK 风格），多个工程并存——主 mod 工程（JipperKeyViewer / JipperOverlayer）、
          加载器工程（*.Loader.Melon / *.Loader.UMM）、Unity 资源工程（*-Unity，导出 AssetBundle）。
        - mod 入口：仓库根 Info.json 声明 Id / DisplayName / Author / Version / AssemblyName / EntryMethod；
          Main.Init(IModLoader loader) 初始化，通过 loader.OnToggle / OnUpdate / OnGUI / OnSaveGUI 挂接游戏生命周期，
          常用全局入口如 Main.Log / Main.Warning / Main.Error、Loader.Instance / Loader.ModPath。
        - Harmony 补丁：使用 0Harmony.dll。本项目用 PatchManager 集中管理——RegisterPatch / RegisterPatches / RegisterLazyPatches
          注册补丁类型，RegisterManualPrefix / RegisterManualPostfix 手动绑定，ApplyAll / UnpatchAll 批量应用；
          通过 GetMethodInfo(类型, 方法名, 参数类型, 泛型) 按签名定位目标（注意方法重载），
          AccessTools.FieldRef / CreatePropertyGetter / CreateMemberGetter 访问游戏私有成员（反射容错，勿直接 FieldInfo.GetValue）。
        - 游戏 API：引用 libs/Libs 下的程序集——Assembly-CSharp.dll 为游戏反编译代码（来自 adofai-libs），UnityEngine.* 为引擎模块；
          游戏版本差异用 VersionSafe（IsV141OrLater 等）分支处理，补丁要同时考虑 v136 / v141+ 两套 API。
        - 资源：Unity 资源工程导出 AssetBundle，运行时 BundleLoader.LoadBundle() 加载（字体/贴图/预制体），
          涉及 FontManager / Overlay 等运行时对象时注意 DontDestroyOnLoad 与场景卸载清理。
        - 只读引用库：adofai-libs 是游戏反编译 DLL 与引用库，绝不要修改其中的 DLL 或反编译源码（source/）。
        - 构建与验证：传统 csproj 用 msbuild（dotnet msbuild 亦可）；Harmony 运行时补丁无法单元测试，
          改动后让用户在游戏里验证；Info.json 版本号与 CHANGELOG 在发布时要同步更新。
        - 反编译 API 知识库：修改游戏逻辑前，先 read_file 阅读「AdofaiKnowledge.md」（可在当前目录、
          父目录或同级 adofai-libs/ 下找到；含核心类索引、补丁目标、版本差异与访问模式），再动手。
          同级 adofai-libs 在工作区之外时，靠 codeagent.json 的 readOnlyDirs 白名单只读访问（勿写入）。

        """;

    /// <summary>ADOFAI 专属工作模式（注入到 /mode 目录，同名配置优先保留用户定义）。</summary>
    public static IReadOnlyList<AgentModeConfig> ExtraModes { get; } =
    [
        new AgentModeConfig
        {
            Name = "moddev",
            Description = "ADOFAI mod 开发（默认）：全功能，编写与修改 mod 逻辑",
            SystemPrompt =
                "你是 CodeAgent，处于 MODDEV 模式：ADOFAI（节奏游戏）mod 开发。围绕 mod 的功能逻辑（设置、UI 覆层、按键显示、生命周期）工作。" +
                "遵循 ADOFAI 开发约定：Info.json 声明入口；Main.Init(IModLoader) 挂接 loader 事件；补丁经 PatchManager 注册；" +
                "引用 libs 下游戏/Unity 程序集，用 AccessTools 反射访问私有成员；版本差异用 VersionSafe 分支。构建用 msbuild，" +
                "改动后如实报告验证结果（Harmony 补丁需用户在游戏内验证）。任务完成或需要提问时调用 stop 工具结束本轮。",
            Tools = null, // 全部工具
        },
        new AgentModeConfig
        {
            Name = "harmony",
            Description = "Harmony 补丁开发：补丁方法与反射访问游戏成员",
            SystemPrompt =
                "你是 CodeAgent，处于 HARMONY 模式：ADOFAI mod 的 Harmony 运行时补丁开发。专注补丁本身：" +
                "用 PatchManager.RegisterPatch / RegisterManualPrefix / RegisterManualPostfix 注册；" +
                "用 GetMethodInfo(类型, 方法名, 参数签名) 精确定位目标方法（注意重载，勿按名字猜测）；" +
                "访问游戏私有字段/属性一律走 AccessTools.FieldRef / CreatePropertyGetter / CreateMemberGetter（反射容错）；" +
                "补丁要同时考虑 v136 / v141+ 的 API 差异（VersionSafe），Prefix 返回 false 可短路原方法、Postfix 用于读取结果。" +
                "构建用 msbuild 验证编译，运行时行为需用户在游戏内确认。任务完成或需要提问时调用 stop 工具结束本轮。",
            Tools = null,
        },
        new AgentModeConfig
        {
            Name = "assetbundle",
            Description = "Unity 资源工程：AssetBundle 打包与运行时加载",
            SystemPrompt =
                "你是 CodeAgent，处于 ASSETBUNDLE 模式：ADOFAI mod 的 Unity 资源工程（*-Unity 目录）开发。" +
                "关注资源管线：AssetBundle 打包（Unity 工程内 BuildAssetBundle 配置、AssetBundleBuild 列表）、" +
                "运行时 BundleLoader.LoadBundle() 加载、字体（FontManager.ScanFonts）与贴图/预制体资源的引用路径、卸载与内存释放。" +
                "修改 Unity 工程文件（.asset/.prefab/.meta）时保持 GUID 与引用一致，勿手工改动 .meta 造成资源丢失。" +
                "Unity 工程无法在命令行完整构建时，说明打包步骤并让用户在 Unity 编辑器里执行。任务完成或需要提问时调用 stop 工具结束本轮。",
            Tools = null,
        },
    ];
}
