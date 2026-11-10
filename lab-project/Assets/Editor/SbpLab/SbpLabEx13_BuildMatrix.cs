// ---------------------------------------------------------------------------
// Example 13 —— 多配置构建矩阵 + 报告聚合（CI 工程化）
//
// 难度：★★★★
//
// 教学目标：
//   1. 把前面学的“压缩策略 / 布局策略”组合成多套配置，一次跑完并对比；
//   2. 学会把构建结果聚合成机器可读的 JSON（可直接进 CI artifact / 看板）；
//   3. 建立“配置组合爆炸”的工程意识：真实项目里要控制在少量、可解释的配置。
//
// 本示例的三套配置：
//   A. per-bundle   : props_a / props_b / shared，LZ4（默认布局）
//   B. merged       : props_all / shared，LZ4（复用 Ex12 的合并任务）
//   C. per-bundle   : 同 A，但用 LZMA
//
// 关键 API：
//   - 每套配置都要 new 一套 BundleBuildContent / BundleBuildParameters / 任务链
//     （它们都是“一次构建”的状态，不能跨构建复用）
//   - JsonUtility 序列化报告
//
// 产物：
//   Artifacts/SBP/ex13-<配置名>-<平台>/
//   Artifacts/SBP/ex13-summary.json
//
// 预期现象：
//   Console 打印 3 行配置对比（bundle 数、总大小、耗时），并生成 summary.json。
//
// 思考题：
//   - 如果把“压缩方式”和“布局”做笛卡尔积，配置数量会怎样增长？如何治理？
//   - CI 里除了耗时/体积，还应该采集哪些指标（失败率、缓存命中、产物 hash 变化）？
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

[Serializable]
public class SbpMatrixRow
{
    public string config;
    public string compression;
    public int bundleCount;
    public long totalBytes;
    public long elapsedMs;
}

[Serializable]
public class SbpMatrixReport
{
    public string builtAt;
    public string platform;
    public List<SbpMatrixRow> rows = new List<SbpMatrixRow>();
}

public static class SbpLabEx13_BuildMatrix
{
    [MenuItem("SBP Lab/Examples/13 - 多配置构建矩阵 + 报告聚合", priority = 113)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();

        var report = new SbpMatrixReport
        {
            builtAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            platform = SbpLabCommon.Target.ToString(),
        };

        // A. 默认布局 + LZ4
        report.rows.Add(BuildConfig("A-per-bundle-LZ4", UnityEngine.BuildCompression.LZ4, false));
        // B. 合并布局 + LZ4（复用 Ex12 的策略）
        report.rows.Add(BuildConfig("B-merged-LZ4", UnityEngine.BuildCompression.LZ4, true));
        // C. 默认布局 + LZMA
        report.rows.Add(BuildConfig("C-per-bundle-LZMA", UnityEngine.BuildCompression.LZMA, false));

        Debug.Log("[Ex13] ===== 配置对比 =====");
        foreach (var row in report.rows)
        {
            Debug.Log($"[Ex13]   {row.config,-20} bundles={row.bundleCount} 总大小={SbpLabCommon.Human(row.totalBytes),9} 耗时={row.elapsedMs} ms");
        }

        var summaryPath = Path.Combine(SbpLabCommon.ArtifactsRoot, "ex13-summary.json");
        Directory.CreateDirectory(SbpLabCommon.ArtifactsRoot);
        File.WriteAllText(summaryPath, JsonUtility.ToJson(report, true));
        Debug.Log("[Ex13] 汇总报告 -> " + summaryPath);
    }

    /// <summary>按一套配置构建一次，返回该配置的指标。</summary>
    static SbpMatrixRow BuildConfig(string configName, UnityEngine.BuildCompression compression, bool merge)
    {
        var outPath = SbpLabCommon.OutputFolder("ex13-" + configName);
        // 每次构建都要全新的 parameters / content / tasks：它们是“一次构建”的状态
        var parameters = new BundleBuildParameters(SbpLabCommon.Target, SbpLabCommon.Group, outPath)
        {
            BundleCompression = compression,
            UseCache = true,
        };
        var content = new BundleBuildContent(SbpLabCommon.ExplicitLayout());
        var tasks = DefaultBuildTasks.Create(DefaultBuildTasks.Preset.AssetBundleCompatible);

        if (merge)
        {
            var insertAt = tasks.Count;
            for (var i = 0; i < tasks.Count; i++)
            {
                if (tasks[i].GetType().Name == "PostDependencyCallback")
                {
                    insertAt = i + 1;
                    break;
                }
            }
            tasks.Insert(insertAt, new MergeBundlesTask("props_all", "props_a", "props_b"));
        }

        var stopwatch = Stopwatch.StartNew();
        var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults results, tasks);
        stopwatch.Stop();

        if (code != ReturnCode.Success)
        {
            Debug.LogError($"[Ex13] 配置 {configName} 构建失败，ReturnCode={code}");
            return new SbpMatrixRow { config = configName, compression = compression.ToString() };
        }

        long totalBytes = 0;
        foreach (var kv in results.BundleInfos)
        {
            var file = SbpLabCommon.ResolveBundleFile(kv.Value);
            if (File.Exists(file)) totalBytes += new FileInfo(file).Length;
        }

        return new SbpMatrixRow
        {
            config = configName,
            compression = compression.ToString(),
            bundleCount = results.BundleInfos.Count,
            totalBytes = totalBytes,
            elapsedMs = stopwatch.ElapsedMilliseconds,
        };
    }
}
#endif
