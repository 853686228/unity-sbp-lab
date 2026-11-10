// ---------------------------------------------------------------------------
// Example 16 —— 重内容压缩对比：LZMA / LZ4 / Uncompressed（冷 + 热 各构建一次）
//
// 难度：★★★
//
// 背景：Ex03 用轻量内容（几 KB）测压缩，数值太小看不出趋势。本示例用
//       Assets/Bundles/Heavy 下从 ARPGTutorial 复制来的
//       Audio（约 26 MB）+ 动画 FBX（约 153 MB，拆成两个 bundle）
//       + 8 张合成 2048² 未压缩贴图（约 96 MB packed）做对比。
//
// 为什么每种压缩要跑两次（和 Ex08 一致）：
//   - 第一次是“冷缓存”（先 PurgeCache）：包含依赖计算/首次压缩等一次性成本；
//   - 第二次是“热缓存”：内容没变，SBP 直接复用 BuildCache。
//   只跑一次的话，先跑的那组会额外承担冷缓存成本，和后跑的不公平；
//   跑冷/热两次既能公平对比压缩成本，又能看到每种压缩各自的缓存收益。
//
// 输出：
//   1. 每种压缩：冷构建耗时 / 热构建耗时 / 总大小；
//   2. 每个 bundle 的体积矩阵；
//   3. 汇总对比（冷耗时比、体积比、缓存加速比）；
//   4. Artifacts/SBP/ex16-summary.json（机器可读报告）。
//
// 注意：
//   1. 这些资源仅用于本地学习实验（来源为 ARPGTutorial / 程序生成），不要随仓库分发；
//   2. 首次运行需要先导入资源 + 生成贴图，会额外花时间（不计入下面的计时）；
//   3. 每次构建都 new 一套 parameters/content，避免上一轮状态污染（坑 #18）；
//   4. PurgeCache 清的是整个工程的 SBP 缓存，跑完其它构建第一次会变慢。
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEditor.Build.Pipeline.Utilities;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

[Serializable]
public class Ex16Row
{
    public string compression;
    public long coldMs;
    public long warmMs;
    public long totalBytes;
    public List<string> bundleSizes = new List<string>();   // "bundleName|bytes"
}

[Serializable]
public class Ex16Report
{
    public string builtAt;
    public string platform;
    public string sourceSummary;
    public List<Ex16Row> rows = new List<Ex16Row>();
}

public static class SbpLabEx16_HeavyCompression
{
    class CompressionCase
    {
        public string Label;
        public UnityEngine.BuildCompression Compression;
    }

    [MenuItem("SBP Lab/Examples/16 - 重内容压缩对比（冷+热，约 179 MB）", priority = 116)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();
        SbpLabHeavySetup.EnsureTextures();   // 生成/校验重内容贴图

        var layout = SbpLabCommon.HeavyLayout();
        if (layout.Length == 0)
        {
            Debug.LogError("[Ex16] 找不到 Assets/Bundles/Heavy 内容，请先从 ARPGTutorial 复制资源");
            return;
        }

        // 打印源内容规模作为参照
        long sourceBytes = 0;
        var bundleInfo = new List<string>();
        foreach (var bundle in layout)
        {
            long bundleBytes = 0;
            foreach (var assetPath in bundle.assetNames)
                bundleBytes += new FileInfo(assetPath).Length;
            sourceBytes += bundleBytes;
            bundleInfo.Add($"{bundle.assetBundleName}({bundle.assetNames.Length} 个资源, {SbpLabCommon.Human(bundleBytes)})");
        }
        var sourceSummary = $"源内容：{string.Join("、", bundleInfo.ToArray())}，合计 {SbpLabCommon.Human(sourceBytes)}";
        Debug.Log("[Ex16] " + sourceSummary);

        var cases = new[]
        {
            new CompressionCase { Label = "LZMA", Compression = UnityEngine.BuildCompression.LZMA },
            new CompressionCase { Label = "LZ4", Compression = UnityEngine.BuildCompression.LZ4 },
            new CompressionCase { Label = "Uncompressed", Compression = UnityEngine.BuildCompression.Uncompressed },
        };

        var report = new Ex16Report
        {
            builtAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            platform = SbpLabCommon.Target.ToString(),
            sourceSummary = sourceSummary,
        };
        var sizesByCase = new Dictionary<string, Dictionary<string, long>>();
        var allBundleNames = new List<string>();

        foreach (var item in cases)
        {
            // ---- 冷缓存：先清空 BuildCache，测“从零构建”的成本 ----
            Debug.Log($"[Ex16] ===== {item.Label}：清空 SBP 缓存，冷构建 =====");
            BuildCache.PurgeCache(false);

            var cold = BuildOnce(item);
            if (cold == null)
            {
                Debug.LogError($"[Ex16] {item.Label} 冷构建失败");
                return;
            }

            // ---- 热缓存：内容/配置完全不变，再构建一次 ----
            Debug.Log($"[Ex16] ===== {item.Label}：内容不变，热构建 =====");
            var warm = BuildOnce(item);
            if (warm == null)
            {
                Debug.LogError($"[Ex16] {item.Label} 热构建失败");
                return;
            }

            if (cold.TotalBytes != warm.TotalBytes)
                Debug.LogWarning($"[Ex16] {item.Label} 冷/热产物大小不一致：{cold.TotalBytes} vs {warm.TotalBytes}");

            sizesByCase[item.Label] = cold.Sizes;
            foreach (var name in cold.Sizes.Keys)
                if (!allBundleNames.Contains(name)) allBundleNames.Add(name);

            var row = new Ex16Row
            {
                compression = item.Label,
                coldMs = cold.ElapsedMs,
                warmMs = warm.ElapsedMs,
                totalBytes = cold.TotalBytes,
            };
            foreach (var kv in cold.Sizes)
                row.bundleSizes.Add(kv.Key + "|" + kv.Value);
            report.rows.Add(row);

            Debug.Log($"[Ex16] {item.Label,-13} 冷={cold.ElapsedMs,6} ms  热={warm.ElapsedMs,6} ms  " +
                      $"总大小={SbpLabCommon.Human(cold.TotalBytes),10}  ({cold.Detail})");
        }

        // ---- 1) bundle × 压缩 体积矩阵 -----------------------------------
        allBundleNames.Sort();
        Debug.Log("[Ex16] ===== 各 bundle 体积矩阵 =====");
        foreach (var bundleName in allBundleNames)
        {
            var cells = new List<string>();
            foreach (var item in cases)
            {
                var sizes = sizesByCase[item.Label];
                var size = sizes.ContainsKey(bundleName) ? sizes[bundleName] : 0;
                cells.Add($"{item.Label}={SbpLabCommon.Human(size)}");
            }
            Debug.Log($"[Ex16]   {bundleName,-22} {string.Join("  ", cells.ToArray())}");
        }

        // ---- 2) 汇总对比（冷耗时 / 体积 / 缓存加速）-----------------------
        Debug.Log("[Ex16] ===== 汇总对比 =====");
        foreach (var row in report.rows)
        {
            var speedup = row.warmMs <= 0 ? 0 : (double)row.coldMs / row.warmMs;
            Debug.Log($"[Ex16]   {row.compression,-13} 冷={row.coldMs,6} ms  热={row.warmMs,6} ms  " +
                      $"缓存加速={speedup:0.0}x  总大小={SbpLabCommon.Human(row.totalBytes),10}");
        }

        var lzma = report.rows.FirstOrDefault(r => r.compression == "LZMA");
        var lz4 = report.rows.FirstOrDefault(r => r.compression == "LZ4");
        var uncompressed = report.rows.FirstOrDefault(r => r.compression == "Uncompressed");
        if (lzma != null && uncompressed != null)
        {
            var sizeRatio = uncompressed.totalBytes == 0 ? 0 : (double)lzma.totalBytes / uncompressed.totalBytes;
            var coldRatio = uncompressed.coldMs == 0 ? 0 : (double)lzma.coldMs / uncompressed.coldMs;
            Debug.Log($"[Ex16]   LZMA vs Uncompressed（冷）：体积为 {sizeRatio:P1}，耗时为 {coldRatio:0.0}x");
        }
        if (lz4 != null && uncompressed != null)
        {
            var sizeRatio = uncompressed.totalBytes == 0 ? 0 : (double)lz4.totalBytes / uncompressed.totalBytes;
            var coldRatio = uncompressed.coldMs == 0 ? 0 : (double)lz4.coldMs / uncompressed.coldMs;
            Debug.Log($"[Ex16]   LZ4  vs Uncompressed（冷）：体积为 {sizeRatio:P1}，耗时为 {coldRatio:0.0}x");
        }
        if (lzma != null && lz4 != null && lz4.coldMs > 0)
            Debug.Log($"[Ex16]   LZMA 比 LZ4（冷）：体积小 {1.0 - (double)lzma.totalBytes / Math.Max(1, lz4.totalBytes):P1}，" +
                      $"耗时多 {(double)lzma.coldMs / lz4.coldMs:0.0}x");

        // ---- 3) 机器可读报告 ----------------------------------------------
        var summaryPath = Path.Combine(SbpLabCommon.ArtifactsRoot, "ex16-summary.json");
        Directory.CreateDirectory(SbpLabCommon.ArtifactsRoot);
        File.WriteAllText(summaryPath, JsonUtility.ToJson(report, true));
        Debug.Log("[Ex16] 汇总报告 -> " + summaryPath);
        Debug.Log("[Ex16] 提示：本组贴图刻意高度可压缩（渐变），真实贴图的压缩率会更低一档");
    }

    class BuildResult
    {
        public long ElapsedMs;
        public long TotalBytes;
        public string Detail;
        public Dictionary<string, long> Sizes;
    }

    /// <summary>用指定压缩方式构建一次，返回耗时/体积明细。</summary>
    static BuildResult BuildOnce(CompressionCase item)
    {
        var outPath = SbpLabCommon.OutputFolder("ex16-" + item.Label);
        var parameters = new BundleBuildParameters(SbpLabCommon.Target, SbpLabCommon.Group, outPath)
        {
            BundleCompression = item.Compression,
            UseCache = true,
        };
        var content = new BundleBuildContent(SbpLabCommon.HeavyLayout());

        var stopwatch = Stopwatch.StartNew();
        var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults results);
        stopwatch.Stop();

        if (code != ReturnCode.Success)
            return null;

        var result = new BuildResult
        {
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            Sizes = new Dictionary<string, long>(),
        };
        var details = new List<string>();
        foreach (var kv in results.BundleInfos)
        {
            var file = SbpLabCommon.ResolveBundleFile(kv.Value);
            var size = File.Exists(file) ? new FileInfo(file).Length : 0;
            result.TotalBytes += size;
            result.Sizes[kv.Key] = size;
            details.Add($"{kv.Key}={SbpLabCommon.Human(size)}");
        }
        result.Detail = string.Join(", ", details.ToArray());
        return result;
    }
}
#endif
