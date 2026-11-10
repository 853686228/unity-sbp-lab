// ---------------------------------------------------------------------------
// SBP Lab 公共设施（Common）
//
// 目的：
//   1. 统一路径与构建输出位置，避免每个示例各写一套；
//   2. 提供“从 AssetBundle 标签生成内容”和“显式布局”两种内容构造方式；
//   3. 提供结果打印 / 批处理包装，让每个教学示例只保留“本课重点”。
//
// 说明：本文件只被 Editor 使用，放在 Assets/Editor 下所以自动进入
//       Assembly-CSharp-Editor，不会打进玩家包。
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;
using UnityEngine.Build.Pipeline;

/// <summary>
/// SBP Lab 的公共工具集合。所有 Example 都通过它获取路径、内容、输出与结果打印。
/// </summary>
public static class SbpLabCommon
{
    // 测试资源固定放在这里；由 SBP Lab/0 - Setup Test Assets 生成。
    public const string BundleRoot = "Assets/Bundles";
    public const string MatPath = BundleRoot + "/Shared/Mat.mat";
    public const string CubeAPath = BundleRoot + "/Props/CubeA.prefab";
    public const string CubeBPath = BundleRoot + "/Props/CubeB.prefab";
    public const string CubeCPath = BundleRoot + "/Props/CubeC.prefab";

    // 所有示例的产物都集中在 Artifacts/SBP 下，便于对比、清理，也不会污染 Assets。
    public const string ArtifactsRoot = "Artifacts/SBP";

    /// <summary>“重内容”目录：从 ARPGTutorial 复制来的音频/动画（约 179 MB），用于体积/压缩/缓存实验。</summary>
    public const string HeavyRoot = BundleRoot + "/Heavy";

    /// <summary>当前激活的构建平台。注意：SBP 必须在目标平台模块已安装时才能构建正确内容。</summary>
    public static BuildTarget Target { get { return EditorUserBuildSettings.activeBuildTarget; } }

    /// <summary>目标平台对应的 BuildTargetGroup（Android -> Android）。</summary>
    public static BuildTargetGroup Group { get { return BuildPipeline.GetBuildTargetGroup(Target); } }

    /// <summary>确保 0 号菜单生成过测试资源；没有就自动补一次，保证示例可独立运行。</summary>
    public static void EnsureLabAssets()
    {
        if (File.Exists(MatPath) && File.Exists(CubeAPath)) return;
        Debug.Log("[SBP Lab] 测试资源不存在，自动执行 SetupTestAssets()");
        SbpLabBuild.SetupTestAssets();
    }

    /// <summary>创建并返回一个产物目录：Artifacts/SBP/&lt;tag&gt;-&lt;平台&gt;。</summary>
    public static string OutputFolder(string tag)
    {
        var dir = Path.Combine(ArtifactsRoot, tag + "-" + Target);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ------------------------------------------------------------------
    // 内容构造：方式 A —— 沿用 Project 窗口里打的 AssetBundle 标签
    // ------------------------------------------------------------------

    /// <summary>
    /// 用项目中已有的 AssetBundle 标签生成构建内容。
    /// 这是最接近“内置管线”的迁移方式：标签在哪，bundle 就在哪。
    /// </summary>
    public static BundleBuildContent ContentFromLabels()
    {
        var builds = ContentBuildInterface.GenerateAssetBundleBuilds();
        if (builds == null || builds.Length == 0)
            throw new InvalidOperationException("没有任何资源带 AssetBundle 标签，请先运行 SBP Lab/0 - Setup Test Assets");
        return new BundleBuildContent(builds);
    }

    // ------------------------------------------------------------------
    // 内容构造：方式 B —— 代码里显式指定布局（不依赖标签）
    // ------------------------------------------------------------------

    /// <summary>
    /// 显式布局：bundle 名称、bundle 内包含哪些资源，全部由代码决定。
    /// 适合“打包规则必须可审计/可配置”的工程（例如从 Excel、配置中心生成布局）。
    /// </summary>
    public static AssetBundleBuild[] ExplicitLayout()
    {
        return new[]
        {
            MakeBundle("props_a", CubeAPath, CubeBPath),
            MakeBundle("props_b", CubeCPath),
            MakeBundle("shared",  MatPath),
        };
    }

    /// <summary>构造单个 bundle 描述。assetNames 必须是 Assets/ 下的相对路径。</summary>
    public static AssetBundleBuild MakeBundle(string bundleName, params string[] assetPaths)
    {
        return new AssetBundleBuild
        {
            assetBundleName = bundleName,
            assetNames = assetPaths,
            // assetBundleVariant 留空；需要 HD/SD 变体时再填（见 Wiki 的变体章节）。
        };
    }

    // ------------------------------------------------------------------
    // 内容构造：方式 C —— 重内容（音频 + 动画 FBX，约 179 MB）
    // ------------------------------------------------------------------

    /// <summary>
    /// 重内容布局：heavy_audio（约 26 MB 音频）+ heavy_art_a/b（动画 FBX，按大小交替拆分）
    /// + heavy_textures（8 张 2048² 未压缩贴图，约 100 MB packed）。
    /// 目的：让压缩率、包体、增量缓存的差异在数据上“看得出来”。
    /// 注意：这些资源仅用于本地学习实验（FBX/WAV 来自 ARPGTutorial，贴图为程序生成），不要随仓库分发。
    /// </summary>
    public static AssetBundleBuild[] HeavyLayout()
    {
        var audio = FindAssetPaths(HeavyRoot + "/Audio", new[] { ".wav", ".mp3", ".ogg" });
        var art = FindAssetPaths(HeavyRoot + "/Art", new[] { ".fbx" });
        var textures = FindAssetPaths(HeavyRoot + "/Textures", new[] { ".png", ".tga", ".jpg", ".jpeg" });

        var builds = new List<AssetBundleBuild>();
        if (audio.Length > 0)
            builds.Add(MakeBundle("heavy_audio", audio));
        if (textures.Length > 0)
            builds.Add(MakeBundle("heavy_textures", textures));

        // 按文件大小交替拆分，尽量让两个 art bundle 体积均衡
        var bySize = art.OrderByDescending(p => new FileInfo(p).Length).ToArray();
        var left = new List<string>();
        var right = new List<string>();
        for (var i = 0; i < bySize.Length; i++)
            (i % 2 == 0 ? left : right).Add(bySize[i]);
        if (left.Count > 0) builds.Add(MakeBundle("heavy_art_a", left.ToArray()));
        if (right.Count > 0) builds.Add(MakeBundle("heavy_art_b", right.ToArray()));

        return builds.ToArray();
    }

    /// <summary>按目录 + 扩展名收集资源路径（显式布局用，不依赖 AssetBundle 标签）。</summary>
    public static string[] FindAssetPaths(string folder, string[] extensions)
    {
        if (!AssetDatabase.IsValidFolder(folder)) return new string[0];
        var result = new List<string>();
        foreach (var guid in AssetDatabase.FindAssets("", new[] { folder }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (AssetDatabase.IsValidFolder(path)) continue;
            if (extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                result.Add(path);
        }
        result.Sort(StringComparer.Ordinal);
        return result.ToArray();
    }

    // ------------------------------------------------------------------
    // 结果打印与文件定位
    // ------------------------------------------------------------------

    /// <summary>
    /// 把 IBundleBuildResults 里的关键信息打出来。
    /// 注意：BundleDetails.FileName 在 1.23 里已经是“含输出目录的相对路径”，
    /// 直接 File.Exists 判断即可，不要再无脑 Path.Combine，否则会重复拼接。
    /// </summary>
    public static void PrintResults(IBundleBuildResults results, string outPath)
    {
        if (results == null)
        {
            Debug.LogError("[SBP Lab] 结果为 null，构建没有正常完成");
            return;
        }

        Debug.Log("[SBP Lab] 构建结果：共 " + results.BundleInfos.Count + " 个 bundle -> " + outPath);
        foreach (var kv in results.BundleInfos)
        {
            var details = kv.Value;
            var file = ResolveBundleFile(details);
            var size = File.Exists(file) ? new FileInfo(file).Length : 0;
            var deps = details.Dependencies == null ? 0 : details.Dependencies.Length;
            Debug.Log(string.Format(
                "[SBP Lab]   {0,-10} hash={1} size={2,9} deps={3}",
                kv.Key, details.Hash, Human(size), deps));
        }
    }

    /// <summary>把 BundleDetails.FileName 解析成磁盘上真实存在的路径。</summary>
    public static string ResolveBundleFile(BundleDetails details)
    {
        if (File.Exists(details.FileName)) return details.FileName;
        return Path.Combine(ArtifactsRoot, details.FileName);
    }

    /// <summary>把字节数变成人类可读的字符串。</summary>
    public static string Human(long bytes)
    {
        if (bytes >= 1024 * 1024) return (bytes / 1024f / 1024f).ToString("0.00") + " MB";
        if (bytes >= 1024) return (bytes / 1024f).ToString("0.0") + " KB";
        return bytes + " B";
    }

    /// <summary>目录大小（用于观察 Library/BuildCache 的增量缓存体积）。</summary>
    public static long DirectorySize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; } catch { /* 文件可能正在被占用，忽略 */ }
        }
        return total;
    }

    // ------------------------------------------------------------------
    // batchmode 包装：异常时用非 0 退出码，方便 CI 感知失败
    // ------------------------------------------------------------------

    /// <summary>
    /// 包一层 try/catch。batchmode 下脚本抛异常默认不一定能让进程返回非 0，
    /// 这里显式 EditorApplication.Exit(1)，让命令行/CI 能正确判断失败。
    /// </summary>
    public static void Run(Action action, string label)
    {
        try
        {
            action();
            Debug.Log("[SBP Lab] batch 完成：" + label);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }
}
#endif
