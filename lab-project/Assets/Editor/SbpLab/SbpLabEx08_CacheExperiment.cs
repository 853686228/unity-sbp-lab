// ---------------------------------------------------------------------------
// Example 08 —— 增量缓存实验：量化“第二次构建为什么快”
//
// 教学目标：
//   1. 亲手测一次“冷缓存 vs 热缓存”的构建耗时；
//   2. 知道缓存放在 Library/BuildCache（可安全删除的本地缓存）；
//   3. 理解 UseCache / BuildCache 是 SBP 增量能力的核心，而不是“编译加速”。
//
// 关键 API：
//   BundleBuildParameters.UseCache = true
//   BuildCache.PurgeCache(prompt: false)   // 直接清空，不弹窗
//
// 实验步骤（脚本自动执行）：
//   1. 清空缓存 -> 构建一次（冷）  记时间 T1
//   2. 什么都不改 -> 再构建一次（热）记时间 T2
//   3. 打印 T1/T2 与缓存目录体积
//
// 预期现象（重内容 + LZMA 实测）：
//   冷构建 ≈ 6.0 s，热构建 ≈ 1.4 s，加速比 ≈ 4.2x；
//   内容总量约 179 MB 源资源（音频/动画来自 ARPGTutorial + 8 张合成未压缩贴图），
//   产物 LZMA 约 8.5 MB。真实项目里差距通常是“几分钟 vs 几秒”。
//
// 注意：
//   - PurgeCache 影响整个工程的 SBP 缓存（所有用 SBP 的构建都会变慢一次），
//     这是本地开发缓存，删掉不会损坏资源；
//   - 不要把它放进正式 CI 的常规流程。
//
// 思考题：
//   - 为什么改了 CubeA 之后，shared 也不需要重打？（提示：TaskCachingUtility 的输入 hash）
//   - 如果改了自定义任务的 Version，缓存会怎样？（提示：任务版本是 key 的一部分）
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using Stopwatch = System.Diagnostics.Stopwatch;   // 只借用 Stopwatch，避免 Debug 二义性
using System.IO;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEditor.Build.Pipeline.Utilities;
using UnityEngine;

public static class SbpLabEx08_CacheExperiment
{
    const string CachePath = "Library/BuildCache";

    [MenuItem("SBP Lab/Examples/08 - 增量缓存实验", priority = 108)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();
        SbpLabHeavySetup.EnsureTextures();   // 保证重内容贴图存在（约 100 MB packed）

        Debug.Log("[Ex08] 缓存目录（清空前）：" + SbpLabCommon.Human(SbpLabCommon.DirectorySize(CachePath)));

        // ---- 1) 清空缓存，构建一次（冷） ---------------------------------
        Debug.Log("[Ex08] 清空 SBP 缓存...");
        BuildCache.PurgeCache(false); // false = 不弹确认框，直接删

        var coldMs = BuildOnce("ex08-cold");
        var cacheAfterCold = SbpLabCommon.DirectorySize(CachePath);
        var coldOutput = SbpLabCommon.OutputFolder("ex08-cold");
        Debug.Log($"[Ex08] 冷构建耗时 {coldMs} ms，缓存体积 {SbpLabCommon.Human(cacheAfterCold)}，产物总大小 {SbpLabCommon.Human(SbpLabCommon.DirectorySize(coldOutput))}");

        // ---- 2) 不做任何修改，再构建一次（热） ---------------------------
        var warmMs = BuildOnce("ex08-warm");
        Debug.Log($"[Ex08] 热构建耗时 {warmMs} ms");

        // ---- 3) 汇总 -----------------------------------------------------
        Debug.Log($"[Ex08] 对比：冷={coldMs} ms，热={warmMs} ms，加速比={(warmMs <= 0 ? "-" : ((double)coldMs / warmMs).ToString("0.0") + "x")}");
        Debug.Log("[Ex08] 说明：内容越重（本例约 179 MB 音频+动画），预热后能省下的时间越明显");
    }

    /// <summary>构建一次并返回耗时（毫秒）。</summary>
    static long BuildOnce(string tag)
    {
        var outPath = SbpLabCommon.OutputFolder(tag);
        var parameters = new BundleBuildParameters(SbpLabCommon.Target, SbpLabCommon.Group, outPath)
        {
            // 故意用 LZMA：压缩成本高，冷构建的“可省略工作”才明显，
            // 能直观看出缓存省掉的到底是什么。生产上通常用 LZ4。
            BundleCompression = UnityEngine.BuildCompression.LZMA,
            UseCache = true, // 增量能力的总开关
        };

        // 优先用重内容（Heavy），这样冷/热构建的绝对耗时差距更明显
        var layout = SbpLabCommon.HeavyLayout();
        if (layout.Length == 0)
        {
            Debug.LogWarning("[Ex08] 未找到 Assets/Bundles/Heavy 内容，回退到轻量布局");
            layout = SbpLabCommon.ExplicitLayout();
        }
        var content = new BundleBuildContent(layout);
        Debug.Log($"[Ex08] {tag}: 内容 bundle 数 = {layout.Length}");

        var stopwatch = Stopwatch.StartNew();
        var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults results);
        stopwatch.Stop();

        if (code != ReturnCode.Success)
            Debug.LogError("[Ex08] 构建失败，ReturnCode=" + code);

        return stopwatch.ElapsedMilliseconds;
    }
}
#endif
