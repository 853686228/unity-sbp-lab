// ---------------------------------------------------------------------------
// Example 02 —— 显式布局：bundle 里装什么，由代码说了算
//
// 教学目标：
//   1. 对比“标签驱动”和“代码显式布局”两种内容来源；
//   2. 理解 BundleBuildContent 内部其实维护三张表：
//        BundleLayout : bundle 名 -> 资源 GUID 列表
//        Addresses    : 资源 GUID -> 内部 id（addressableName）
//        Assets/Scenes: 参与构建的资源/场景清单
//   3. 学会用 ContentPipeline + 显式布局构建，而不是依赖 Project 窗口里的标签。
//
// 关键 API：
//   new BundleBuildContent(AssetBundleBuild[])
//   ContentPipeline.BuildAssetBundles(parameters, content, out results)
//
// 预期现象：
//   Console 打印 layout 里每个 bundle 的资源数量，以及构建结果。
//   注意：这次构建完全不看 Project 窗口里的 AssetBundle 标签；
//   即使你把标签全删掉，这个示例依然能构建出 3 个 bundle。
//
// 思考题：
//   - 如果显式布局里的 bundle 名和资源标签冲突，最终生效的是谁？（答案：显式布局）
//   - BundleLayout 里存的是 GUID 而不是路径，这带来什么好处？（提示：重命名资产不变）
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;

public static class SbpLabEx02_ExplicitLayout
{
    [MenuItem("SBP Lab/Examples/02 - ContentPipeline 显式布局", priority = 102)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();

        var outPath = SbpLabCommon.OutputFolder("ex02-explicit");

        // 1) 代码里定义布局：props_a 装 CubeA+CubeB，props_b 装 CubeC，shared 装材质
        var layout = SbpLabCommon.ExplicitLayout();
        var content = new BundleBuildContent(layout);

        // 2) 构建参数：平台、平台组、输出目录。
        //    注意 BundleCompression 的类型是 UnityEngine.BuildCompression，
        //    如果文件里同时 using 了 UnityEditor.Build.Content，会撞名，需写全名。
        var parameters = new BundleBuildParameters(SbpLabCommon.Target, SbpLabCommon.Group, outPath)
        {
            BundleCompression = UnityEngine.BuildCompression.LZ4,
            UseCache = true, // 默认就是 true，这里显式写出来提醒你增量缓存的存在
        };

        // 3) 打印显式布局的内部结构，帮助理解 BundleBuildContent
        Debug.Log("[Ex02] 显式布局明细：");
        foreach (var pair in content.BundleLayout)
        {
            // pair.Value 是 List<GUID>，这里只打印数量，避免把 GUID 类型带进示例
            Debug.Log($"[Ex02]   bundle={pair.Key,-10} 资源数={pair.Value.Count}");
        }
        Debug.Log($"[Ex02]   参与构建的资源总数={content.Assets.Count}，address 映射数={content.Addresses.Count}");

        // 4) 构建
        var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults results);
        if (code != ReturnCode.Success)
        {
            Debug.LogError("[Ex02] 构建失败，ReturnCode=" + code);
            return;
        }

        SbpLabCommon.PrintResults(results, outPath);

        // 5) 验证依赖：props_a / props_b 都依赖 shared
        foreach (var kv in results.BundleInfos)
        {
            var deps = kv.Value.Dependencies;
            var depText = deps == null || deps.Length == 0 ? "(无)" : string.Join(", ", deps);
            Debug.Log($"[Ex02] 结果依赖 {kv.Key,-10} -> {depText}");
        }
    }
}
#endif
