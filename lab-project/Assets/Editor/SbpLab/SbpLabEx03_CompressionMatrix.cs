// ---------------------------------------------------------------------------
// Example 03 —— 压缩方式对比矩阵：LZMA / LZ4 / Uncompressed
//
// 教学目标：
//   1. 搞清 BuildCompression 三种基础模式的取舍：
//        LZMA        : 压缩率最高，但加载时需要整体解压（适合 CDN 下载、不常驻）
//        LZ4         : 分块压缩，可随机读取（移动端最常用）
//        Uncompressed: 不压缩，体积最大、加载最快（本地调试 / 极低端设备）
//   2. 同一份内容构建三次，用真实数字建立“体积 vs 时间”的直觉。
//
// 关键 API：
//   BundleBuildParameters.BundleCompression
//
// 预期现象：
//   Artifacts/SBP/ex03-{LZMA,LZ4,Uncompressed}-<平台>/ 三套产物；
//   Console 打印每个 bundle 在三种压缩下的体积对比表。
//
// 注意：
//   - 这里的体积包含 Unity 的内置资源（shared 里主要是 shader/引擎数据），
//     所以你能看到 LZMA 和 Uncompressed 差距很大；
//   - 压缩只影响“磁盘/网络体积”和“加载时是否解压”，不影响资源本身。
//
// 思考题：
//   - 你的项目里，哪些 bundle 适合 LZMA？（提示：更新频率低、整包下载）
//   - LZ4 的分块大小在哪里决定？（提示：ArchiveAndCompressBundles 任务）
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;

public static class SbpLabEx03_CompressionMatrix
{
    /// <summary>一次压缩实验的描述。</summary>
    class CompressionCase
    {
        public string Label;
        public UnityEngine.BuildCompression Compression;
    }

    [MenuItem("SBP Lab/Examples/03 - 压缩方式对比矩阵", priority = 103)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();

        var cases = new[]
        {
            new CompressionCase { Label = "LZMA",         Compression = UnityEngine.BuildCompression.LZMA },
            new CompressionCase { Label = "LZ4",          Compression = UnityEngine.BuildCompression.LZ4 },
            new CompressionCase { Label = "Uncompressed", Compression = UnityEngine.BuildCompression.Uncompressed },
        };

        // matrix[压缩方式][bundle 名] = 文件字节数
        var matrix = new Dictionary<string, Dictionary<string, long>>();
        // 每种压缩的总耗时，用来观察 LZMA 的 CPU 代价
        var timings = new Dictionary<string, long>();

        foreach (var item in cases)
        {
            var outPath = SbpLabCommon.OutputFolder("ex03-" + item.Label);
            var content = new BundleBuildContent(SbpLabCommon.ExplicitLayout());
            var parameters = new BundleBuildParameters(SbpLabCommon.Target, SbpLabCommon.Group, outPath)
            {
                BundleCompression = item.Compression,
                UseCache = true,
            };

            var stopwatch = Stopwatch.StartNew();
            var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults results);
            stopwatch.Stop();
            timings[item.Label] = stopwatch.ElapsedMilliseconds;

            if (code != ReturnCode.Success)
            {
                Debug.LogError($"[Ex03] {item.Label} 构建失败，ReturnCode={code}");
                return;
            }

            var sizes = new Dictionary<string, long>();
            foreach (var kv in results.BundleInfos)
            {
                var file = SbpLabCommon.ResolveBundleFile(kv.Value);
                sizes[kv.Key] = System.IO.File.Exists(file) ? new System.IO.FileInfo(file).Length : 0;
            }
            matrix[item.Label] = sizes;

            Debug.Log($"[Ex03] {item.Label,-13} 构建完成，耗时 {stopwatch.ElapsedMilliseconds,5} ms -> {outPath}");
        }

        // ---- 打印对比表 ----------------------------------------------------
        Debug.Log("[Ex03] ===== 压缩对比（单位：字节）=====");
        var bundleNames = new List<string>();
        foreach (var kv in matrix[cases[0].Label]) bundleNames.Add(kv.Key);
        bundleNames.Sort();

        foreach (var bundle in bundleNames)
        {
            var cells = new List<string>();
            foreach (var item in cases)
            {
                var size = matrix[item.Label].ContainsKey(bundle) ? matrix[item.Label][bundle] : 0;
                cells.Add($"{item.Label}={size}");
            }
            Debug.Log($"[Ex03]   {bundle,-10} " + string.Join("  ", cells));
        }

        foreach (var item in cases)
        {
            long total = 0;
            foreach (var size in matrix[item.Label].Values) total += size;
            Debug.Log($"[Ex03]   总大小 {item.Label,-13} {SbpLabCommon.Human(total),10}   构建耗时 {timings[item.Label]} ms");
        }
    }
}
#endif
