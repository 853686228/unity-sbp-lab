// ---------------------------------------------------------------------------
// Example 04 —— 每个 bundle 用不同压缩策略（自定义 BuildParameters）
//
// 教学目标：
//   1. 学会继承 BundleBuildParameters 并重写 GetCompressionForIdentifier；
//   2. 理解 SBP 的扩展点设计：参数、任务、缓存都是“可替换的零件”；
//   3. 认识 identifier 的语义——ArchiveAndCompressBundles 传入的是
//      **bundle 名**（不是文件名、不是路径）。
//
// 关键 API：
//   class MyParameters : BundleBuildParameters
//   public override BuildCompression GetCompressionForIdentifier(string identifier)
//
// 典型业务场景：
//   - 首包内置 bundle 用 LZ4（加载快、可随机读）；
//   - 远端整包下载的 bundle 用 LZMA（省流量）；
//   - 极低端设备或调试期用 Uncompressed（快速验证）。
//
// 预期现象：
//   props_a / props_b 走 LZ4，shared 走 LZMA；Console 打印策略和实际体积。
//
// 思考题：
//   - 如果 GetCompressionForIdentifier 返回的压缩类型和 BundleCompression 默认值
//     不一致，缓存 key 会受影响吗？（提示：看 ArchiveAndCompressBundles 的缓存逻辑）
//   - 为什么 SBP 不在构建后再压缩一次，而是按 bundle 决定压缩方式？
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;

public static class SbpLabEx04_PerBundleCompression
{
    /// <summary>
    /// 自定义参数类：把“bundle 名 -> 压缩方式”的映射塞进来。
    /// 注意：BundleBuildParameters 的构造函数签名是 (target, group, outputFolder)。
    /// </summary>
    class PerBundleCompressionParameters : BundleBuildParameters
    {
        readonly Dictionary<string, UnityEngine.BuildCompression> m_Overrides;

        public PerBundleCompressionParameters(
            BuildTarget target,
            BuildTargetGroup group,
            string outputFolder,
            Dictionary<string, UnityEngine.BuildCompression> overrides)
            : base(target, group, outputFolder)
        {
            m_Overrides = overrides;
        }

        /// <summary>
        /// SBP 在压缩阶段会对每个 bundle 调用这个方法，identifier 就是 bundle 名。
        /// 没配置的 bundle 回退到默认策略（BundleCompression）。
        /// </summary>
        public override UnityEngine.BuildCompression GetCompressionForIdentifier(string identifier)
        {
            if (m_Overrides != null && m_Overrides.TryGetValue(identifier, out var compression))
                return compression;
            return base.GetCompressionForIdentifier(identifier);
        }
    }

    [MenuItem("SBP Lab/Examples/04 - 每个 bundle 独立压缩", priority = 104)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();

        var outPath = SbpLabCommon.OutputFolder("ex04-per-bundle");

        // 策略表：props_* 是热更/常驻内容，用 LZ4；shared 是整包内容，用 LZMA
        var overrides = new Dictionary<string, UnityEngine.BuildCompression>
        {
            { "props_a", UnityEngine.BuildCompression.LZ4 },
            { "props_b", UnityEngine.BuildCompression.LZ4 },
            { "shared",  UnityEngine.BuildCompression.LZMA },
        };

        var parameters = new PerBundleCompressionParameters(
            SbpLabCommon.Target, SbpLabCommon.Group, outPath, overrides)
        {
            // 默认值：映射表里没有的 bundle 用 LZ4
            BundleCompression = UnityEngine.BuildCompression.LZ4,
            UseCache = true,
        };

        var content = new BundleBuildContent(SbpLabCommon.ExplicitLayout());
        var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults results);
        if (code != ReturnCode.Success)
        {
            Debug.LogError("[Ex04] 构建失败，ReturnCode=" + code);
            return;
        }

        Debug.Log("[Ex04] 每个 bundle 的压缩策略：");
        foreach (var kv in results.BundleInfos)
        {
            var compression = overrides.TryGetValue(kv.Key, out var configured)
                ? Describe(configured)
                : "默认(LZ4)";
            var file = SbpLabCommon.ResolveBundleFile(kv.Value);
            var size = System.IO.File.Exists(file) ? new System.IO.FileInfo(file).Length : 0;
            Debug.Log($"[Ex04]   {kv.Key,-10} 压缩={compression,-13} 大小={SbpLabCommon.Human(size),9}");
        }
    }

    /// <summary>把 BuildCompression 结构体转成可读名称（结构体 ToString 只返回类型名）。</summary>
    static string Describe(UnityEngine.BuildCompression compression)
    {
        if (compression.Equals(UnityEngine.BuildCompression.LZMA)) return "LZMA";
        if (compression.Equals(UnityEngine.BuildCompression.LZ4)) return "LZ4";
        if (compression.Equals(UnityEngine.BuildCompression.Uncompressed)) return "Uncompressed";
        return "Custom";
    }
}
#endif
