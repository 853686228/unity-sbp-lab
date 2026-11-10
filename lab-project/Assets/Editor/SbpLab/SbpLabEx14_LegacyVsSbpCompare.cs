// ---------------------------------------------------------------------------
// Example 14 —— 旧 BuildPipeline vs SBP（Compatibility）产物对比
//
// 难度：★★★
//
// 教学目标：
//   1. 同一份内容、同一套选项，分别用旧管线和 SBP 构建，逐文件对比产物；
//   2. 分清“bundle 内容差异”和“清单/杂项差异”；
//   3. 把官方 UpgradeGuide 的结论用实测数据验证一遍。
//
// 官方 UpgradeGuide 的关键差异（SBP 1.21+）：
//   - 旧管线会在输出目录生成一个 AssetBundle 形式的 manifest bundle（文件名=输出目录名）；
//     SBP 不生成，改用内存对象 CompatibilityAssetBundleManifest；
//   - 旧管线给每个 bundle 写 .manifest YAML 文本；SBP 不写，只有
//     CompatibilityBuildPipeline 会写一个“目录名.manifest”；
//   - 加载路径语义不同：SBP 默认只支持完整资源路径，旧管线还支持短文件名/带扩展名；
//   - Variants / BuildReport / IgnoreTypeTreeChanges 等选项 SBP 不支持。
//
// 实测踩坑（本示例代码里已规避）：
//   旧管线的返回值 AssetBundleManifest 是从「刚构建出来的 manifest bundle」里加载的
//   UnityEngine.Object。如果你在同一个 session 里再跑一次别的构建（会触发
//   UnloadUnusedAssets），这个 manifest 会变成“假 null”（== null 为 true）。
//   所以本示例在**构建返回后立刻**把需要的信息快照成托管数据，再跑另一侧构建。
//
// 预期现象：
//   Console 打印两侧 manifest 信息、文件清单、大小、MD5，以及同名文件是否一致。
//
// 思考题：
//   - 如果 bundle 文件逐字节一致，换管线的风险主要在哪？（清单/加载语义/回调/选项）
//   - 为什么 SBP 砍掉 manifest bundle？代价是运行时怎么拿依赖关系？
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEngine;
using UnityEngine.Build.Pipeline;   // CompatibilityAssetBundleManifest 在运行时程序集里

public static class SbpLabEx14_LegacyVsSbpCompare
{
    /// <summary>构建完成后立刻抓取的托管快照（避免 UnityEngine.Object 生命周期问题）。</summary>
    class ManifestSnapshot
    {
        public readonly List<string> Bundles = new List<string>();
        public readonly Dictionary<string, string> Hashes = new Dictionary<string, string>();
        public readonly Dictionary<string, string> Dependencies = new Dictionary<string, string>();
    }

    [MenuItem("SBP Lab/Examples/14 - 旧管线 vs SBP 产物对比", priority = 114)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();

        var target = SbpLabCommon.Target;
        Debug.Log($"[Ex14] 当前活动平台={EditorUserBuildSettings.activeBuildTarget}，构建平台={target}");

        var legacyDir = Path.Combine(SbpLabCommon.ArtifactsRoot, "cmp-legacy-" + target);
        var sbpDir = Path.Combine(SbpLabCommon.ArtifactsRoot, "cmp-sbp-" + target);
        ResetDir(legacyDir);
        ResetDir(sbpDir);

        Debug.Log("[Ex14] ===== 1) 旧 BuildPipeline 构建 =====");
        var legacySnapshot = BuildLegacy(legacyDir, target);

        Debug.Log("[Ex14] ===== 2) CompatibilityBuildPipeline 构建 =====");
        var sbpSnapshot = BuildSbp(sbpDir, target);

        if (legacySnapshot == null || sbpSnapshot == null)
        {
            Debug.LogError($"[Ex14] 有一侧构建失败：legacyNull={legacySnapshot == null}, sbpNull={sbpSnapshot == null}");
            return;
        }

        Dump("旧管线", legacySnapshot);
        Dump("SBP", sbpSnapshot);
        CompareFiles(legacyDir, sbpDir);
        CompareBundleContents(legacyDir, sbpDir, legacySnapshot.Bundles);
    }

    /// <summary>
    /// 分别加载两侧的 bundle，对比内部资源名集合。
    /// 注意：目标平台必须是 Editor 所在平台，否则 bundle 加载不了。
    /// </summary>
    static void CompareBundleContents(string legacyDir, string sbpDir, List<string> bundleNames)
    {
        Debug.Log("[Ex14] ===== 4) bundle 内部资源集合对比 =====");
        foreach (var bundleName in bundleNames)
        {
            var legacyPath = Path.GetFullPath(Path.Combine(legacyDir, bundleName));
            var sbpPath = Path.GetFullPath(Path.Combine(sbpDir, bundleName));
            if (!File.Exists(legacyPath) || !File.Exists(sbpPath)) continue;

            var legacyAssets = LoadAssetNames(legacyPath);
            var sbpAssets = LoadAssetNames(sbpPath);
            var onlyLegacy = legacyAssets.Except(sbpAssets).ToList();
            var onlySbp = sbpAssets.Except(legacyAssets).ToList();

            Debug.Log($"[Ex14] {bundleName,-10} legacy资源数={legacyAssets.Count} sbp资源数={sbpAssets.Count} " +
                      $"仅legacy有={onlyLegacy.Count} 仅sbp有={onlySbp.Count}");
            foreach (var asset in onlyLegacy) Debug.Log($"[Ex14]   仅 legacy: {asset}");
            foreach (var asset in onlySbp) Debug.Log($"[Ex14]   仅 sbp: {asset}");
        }
    }

    static List<string> LoadAssetNames(string path)
    {
        var bundle = AssetBundle.LoadFromFile(path);
        if (bundle == null)
        {
            Debug.LogWarning("[Ex14] 无法加载 bundle（平台不匹配？）：" + path);
            return new List<string>();
        }
        var names = bundle.GetAllAssetNames().ToList();
        bundle.Unload(true);
        return names;
    }

    // ------------------------------------------------------------------
    // 两侧构建：返回托管快照（构建完立刻读取，避免对象被卸载）
    // ------------------------------------------------------------------

    static ManifestSnapshot BuildLegacy(string dir, BuildTarget target)
    {
        var abs = Path.GetFullPath(dir);
#pragma warning disable 618
        var manifest = BuildPipeline.BuildAssetBundles(
            abs, BuildAssetBundleOptions.ChunkBasedCompression, target);
#pragma warning restore 618
        if (manifest == null)
        {
            Debug.LogError("[Ex14] legacy build 返回 null");
            return null;
        }
        return Snapshot(manifest.GetAllAssetBundles(), manifest.GetAssetBundleHash,
            manifest.GetAllDependencies);
    }

    static ManifestSnapshot BuildSbp(string dir, BuildTarget target)
    {
        var manifest = CompatibilityBuildPipeline.BuildAssetBundles(
            dir, BuildAssetBundleOptions.ChunkBasedCompression, target);
        if (manifest == null)
        {
            Debug.LogError("[Ex14] sbp build 返回 null");
            return null;
        }
        return Snapshot(manifest.GetAllAssetBundles(), manifest.GetAssetBundleHash,
            manifest.GetAllDependencies);
    }

    static ManifestSnapshot Snapshot(
        string[] bundles,
        Func<string, Hash128> getHash,
        Func<string, string[]> getDeps)
    {
        var snapshot = new ManifestSnapshot();
        foreach (var bundle in bundles)
        {
            snapshot.Bundles.Add(bundle);
            snapshot.Hashes[bundle] = getHash(bundle).ToString();
            var deps = getDeps(bundle);
            snapshot.Dependencies[bundle] = deps == null || deps.Length == 0
                ? "(无)"
                : string.Join(", ", deps);
        }
        return snapshot;
    }

    // ------------------------------------------------------------------
    // 输出与对比
    // ------------------------------------------------------------------

    static void Dump(string label, ManifestSnapshot snapshot)
    {
        Debug.Log($"[Ex14] {label} manifest 中共 {snapshot.Bundles.Count} 个 bundle：");
        foreach (var bundle in snapshot.Bundles)
        {
            Debug.Log($"[Ex14]   {label} {bundle,-10} hash={snapshot.Hashes[bundle]} deps={snapshot.Dependencies[bundle]}");
        }
    }

    static void CompareFiles(string legacyDir, string sbpDir)
    {
        Debug.Log("[Ex14] ===== 3) 文件清单对比 =====");
        var legacyFiles = ListFiles(legacyDir);
        var sbpFiles = ListFiles(sbpDir);
        var allNames = legacyFiles.Keys.Union(sbpFiles.Keys).OrderBy(x => x).ToList();

        foreach (var name in allNames)
        {
            legacyFiles.TryGetValue(name, out var legacyPath);
            sbpFiles.TryGetValue(name, out var sbpPath);

            var legacyDesc = legacyPath == null ? "(无)" : $"{new FileInfo(legacyPath).Length,8} B {Md5(legacyPath)}";
            var sbpDesc = sbpPath == null ? "(无)" : $"{new FileInfo(sbpPath).Length,8} B {Md5(sbpPath)}";
            var verdict = legacyPath == null || sbpPath == null
                ? "← 仅一侧有"
                : (Md5(legacyPath) == Md5(sbpPath) ? "字节一致" : "内容不同");
            Debug.Log($"[Ex14] {name,-42} legacy: {legacyDesc}  |  sbp: {sbpDesc}  {verdict}");
        }
    }

    static Dictionary<string, string> ListFiles(string dir)
    {
        var map = new Dictionary<string, string>();
        if (!Directory.Exists(dir)) return map;
        foreach (var path in Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly))
            map[Path.GetFileName(path)] = path;
        return map;
    }

    static string Md5(string path)
    {
        using (var md5 = MD5.Create())
        {
            var bytes = File.ReadAllBytes(path);
            return BitConverter.ToString(md5.ComputeHash(bytes)).Replace("-", "").Substring(0, 12).ToLowerInvariant();
        }
    }

    static void ResetDir(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
    }
}
#endif
