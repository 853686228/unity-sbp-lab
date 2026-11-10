// ---------------------------------------------------------------------------
// Example 15 —— Preset 对比：内置 shader / MonoScript 抽取到底影响什么
//
// 难度：★★★
//
// 背景：DefaultBuildTasks 有 4 个 Preset：
//   PlayerScriptsOnly                   只编译玩家脚本，不产 bundle
//   AssetBundleCompatible               ← 默认；shaderTask=false, monoScriptTask=false
//   AssetBundleBuiltInShaderExtraction  shaderTask=true
//   AssetBundleShaderAndScriptExtraction shaderTask=true, monoScriptTask=true
//
// “内置 shader 抽取”到底做什么：
//   CreateBuiltInShadersBundle("UnityBuiltIn.bundle") 会扫描依赖数据里
//   guid == 0000000000000000f000000000000000（resources/unity_builtin_extra）的
//   referencedObject，把它们放进 IBundleExplictObjectLayout；
//   UpdateBundleObjectLayout 再把这些 Object 从原来的 asset bundle 里“搬”到一个
//   只有 Object、没有 Asset 的独立 bundle（UnityBuiltIn.bundle）。
//
// 不抽取（默认 AssetBundleCompatible）会怎样：
//   unity_builtin_extra 里的对象（Standard shader、默认材质、内置贴图等）会作为
//   普通引用被**打进每个引用它的 bundle** —— 即“每个包各带一份 Standard shader”。
//   包体变大、运行时多份副本，但每个 bundle 自包含、无需额外依赖。
//
// 抽取之后：
//   所有引用内置资源的 bundle 都改为依赖 UnityBuiltIn.bundle；
//   包体去重，但运行时必须**先加载 UnityBuiltIn.bundle**，否则依赖解析失败；
//   它没有 Asset（BundleLayout 里 asset 列表为空），不能 LoadAsset，只能当依赖。
//
// 本示例三种 preset 各构建一次，打印 bundle 清单/大小/依赖，直观对比。
// 注意：本 lab 的 prefab 没有挂 MonoBehaviour，所以
//       AssetBundleShaderAndScriptExtraction 和 BuiltInShaderExtraction 的差异
//       可能看不出来（没有 MonoScript 需要抽取）。
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;

public static class SbpLabEx15_PresetComparison
{
    [MenuItem("SBP Lab/Examples/15 - Preset 对比（内置 shader 抽取）", priority = 115)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();

        Compare(DefaultBuildTasks.Preset.AssetBundleCompatible);
        Compare(DefaultBuildTasks.Preset.AssetBundleBuiltInShaderExtraction);
        Compare(DefaultBuildTasks.Preset.AssetBundleShaderAndScriptExtraction);

        Debug.Log("[Ex15] 结论：抽取后会出现一个无 Asset 的 UnityBuiltIn.bundle；" +
                  "所有引用内置 shader 的 bundle 都会依赖它，必须作为依赖先加载。");
    }

    static void Compare(DefaultBuildTasks.Preset preset)
    {
        var outPath = SbpLabCommon.OutputFolder("ex15-" + preset);
        var parameters = new BundleBuildParameters(SbpLabCommon.Target, SbpLabCommon.Group, outPath)
        {
            BundleCompression = UnityEngine.BuildCompression.LZ4,
            UseCache = true,
        };
        var content = new BundleBuildContent(SbpLabCommon.ExplicitLayout());
        var tasks = DefaultBuildTasks.Create(preset);

        var code = ContentPipeline.BuildAssetBundles(
            parameters, content, out IBundleBuildResults results, tasks);
        if (code != ReturnCode.Success)
        {
            Debug.LogError($"[Ex15] {preset} 构建失败，ReturnCode={code}");
            return;
        }

        Debug.Log($"[Ex15] ===== {preset}（{tasks.Count} 个任务）=====");
        long total = 0;
        foreach (var kv in results.BundleInfos.OrderBy(x => x.Key))
        {
            var file = SbpLabCommon.ResolveBundleFile(kv.Value);
            var size = File.Exists(file) ? new FileInfo(file).Length : 0;
            total += size;
            var deps = kv.Value.Dependencies == null || kv.Value.Dependencies.Length == 0
                ? "(无)"
                : string.Join(", ", kv.Value.Dependencies);
            Debug.Log($"[Ex15]   {kv.Key,-26} size={SbpLabCommon.Human(size),9} deps={deps}");
        }
        Debug.Log($"[Ex15]   合计 {results.BundleInfos.Count} 个 bundle，总大小 {SbpLabCommon.Human(total)}");
    }
}
#endif
