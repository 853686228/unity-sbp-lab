// ---------------------------------------------------------------------------
// Example 01 —— 最小平替：只改一行 API，就切到 SBP
//
// 教学目标：
//   1. 认识 CompatibilityBuildPipeline：它是 BuildPipeline.BuildAssetBundles 的
//      “行为对齐版”，参数、返回值、产物结构都一致，迁移成本最低；
//   2. 理解 AssetBundleManifest 仍然存在，可以用它查 bundle 列表和依赖关系。
//
// 关键 API：
//   CompatibilityBuildPipeline.BuildAssetBundles(outputPath, options, target)
//     -> 返回 AssetBundleManifest（和内置管线完全一样）
//
// 预期现象：
//   Artifacts/SBP/ex01-compat-<平台>/ 下出现 3 个 bundle + .manifest；
//   Console 打印每个 bundle 的名称和依赖。
//
// 思考题：
//   - 这行代码背后的任务链是怎么来的？（提示：读 DefaultBuildTasks.Create）
//   - 如果只想要 SBP 的增量缓存，不想改任何代码，够不够用？
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using System.Diagnostics;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEngine;

public static class SbpLabEx01_CompatibilityMinimal
{
    [MenuItem("SBP Lab/Examples/01 - Compatibility 最小平替", priority = 101)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();

        var outPath = SbpLabCommon.OutputFolder("ex01-compat");
        var target = SbpLabCommon.Target;

        // ↓↓↓ 迁移的核心：把 BuildPipeline.BuildAssetBundles 换成 CompatibilityBuildPipeline 的同名方法
        var stopwatch = Stopwatch.StartNew();
        var manifest = CompatibilityBuildPipeline.BuildAssetBundles(
            outPath,
            BuildAssetBundleOptions.ChunkBasedCompression,
            target);
        stopwatch.Stop();

        if (manifest == null)
        {
            UnityEngine.Debug.LogError("[Ex01] 构建失败，manifest 为 null。检查目标平台模块是否安装。");
            return;
        }

        UnityEngine.Debug.Log($"[Ex01] 构建完成，耗时 {stopwatch.ElapsedMilliseconds} ms，输出 {outPath}");

        // manifest 是内置管线时代就有的 AssetBundleManifest，用法完全没变
        foreach (var bundle in manifest.GetAllAssetBundles())
        {
            var deps = manifest.GetAllDependencies(bundle);
            var depText = deps == null || deps.Length == 0 ? "(无)" : string.Join(", ", deps);
            UnityEngine.Debug.Log($"[Ex01] bundle={bundle,-10} 依赖={depText}");
        }
    }
}
#endif
