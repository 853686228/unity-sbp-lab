// ---------------------------------------------------------------------------
// Example 10 —— Content File 管线（Addressables CCD / 内容归档同款）
//
// 难度：★★★★★
//
// 背景：SBP 有两条产物形态截然不同的管线：
//   A. AssetBundle 管线   DefaultBuildTasks.Create(Preset.AssetBundleCompatible)
//      -> 按 bundle 布局产出 assets bundle，适合传统 AssetBundle 热更。
//   B. Content File 管线  DefaultBuildTasks.ContentFileCompatible()（Unity 2022.2+）
//      -> 按“资源共现关系”聚类（clustering），产出 content archive / content file，
//         是 Addressables 远程内容（CCD）与差异更新的底层形态。
//
// 教学目标：
//   1. 认识 ContentFileCompatible 任务链：ClusterBuildLayout 取代
//      GenerateBundlePacking/Commands/Maps 这一组“按 bundle 布局”任务；
//   2. 学会传入两个必需上下文对象：
//        IDeterministicIdentifiers : ContentFileIdentifiers
//        IClusterOutput             : ClusterOutput
//   3. 理解 cluster 的含义：被同一批资源共同引用的 Object 会被聚到一起，
//      从而减少内容更新时需要重新发布的数据量。
//
// 关键 API：
//   DefaultBuildTasks.ContentFileCompatible()
//   new ContentFileIdentifiers()      // 来自 UnityEditor.Build.Pipeline.Utilities
//   new ClusterOutput()               // 来自 UnityEditor.Build.Pipeline.Tasks，实现 IClusterOutput
//   ContentPipeline.BuildAssetBundles(params, content, out results, tasks,
//                                      new ContentFileIdentifiers(), clusterOutput)
//
// 预期现象：
//   - 产物目录里出现以 cluster hash 命名的文件；
//   - Console 打印 cluster 数量、每个产物的 hash/大小；
//   - 和 Ex02 的 3 个 bundle 相比，产物形态和命名完全不同。
//
// 注意：
//   - Content File 管线不是“AssetBundle 的替代品”这么简单：它不写 bundle 名布局，
//     运行时要配合 Addressables 的 content catalog 使用；
//   - 这也是 Addressables “更新构建（content update build）”能只发布增量内容的根因。
//
// 思考题：
//   - 为什么 content file 管线需要 ContentFileIdentifiers 而不是默认的 Unity5PackedIdentifiers？
//   - 如果两个 cluster 里出现同一个 Object，会发生什么？（提示：聚类按“共同引用集合”哈希）
// ---------------------------------------------------------------------------
#if UNITY_EDITOR
#if UNITY_2022_2_OR_NEWER

using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEditor.Build.Pipeline.Tasks;
using UnityEditor.Build.Pipeline.Utilities;
using UnityEngine;

public static class SbpLabEx10_ContentFilePipeline
{
    [MenuItem("SBP Lab/Examples/10 - Content File 管线（Addressables CCD 同款）", priority = 110)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();

        var outPath = SbpLabCommon.OutputFolder("ex10-content-file");
        var parameters = new BundleBuildParameters(SbpLabCommon.Target, SbpLabCommon.Group, outPath)
        {
            BundleCompression = UnityEngine.BuildCompression.LZ4,
            UseCache = true,
        };
        // 显式布局仍然要传：cluster 管线用它来知道“有哪些资源参与构建”
        var content = new BundleBuildContent(SbpLabCommon.ExplicitLayout());

        // 1) 换任务链：ContentFileCompatible 用 ClusterBuildLayout 取代 bundle 布局
        var tasks = DefaultBuildTasks.ContentFileCompatible();

        // 2) 两个必需上下文对象：
        //    - ContentFileIdentifiers：MonoScript 排到文件头部，是 content file 的要求
        //    - ClusterOutput：接收 “Object -> Cluster” 和 “Object -> LocalID” 映射
        var identifiers = new ContentFileIdentifiers();
        var clusterOutput = new ClusterOutput();

        Debug.Log($"[Ex10] 任务链共 {tasks.Count} 个任务（最后一个 {tasks[tasks.Count - 1].GetType().Name}）");

        var code = ContentPipeline.BuildAssetBundles(
            parameters, content, out IBundleBuildResults results, tasks, identifiers, clusterOutput);

        if (code != ReturnCode.Success)
        {
            Debug.LogError("[Ex10] Content File 构建失败，ReturnCode=" + code);
            return;
        }

        Debug.Log($"[Ex10] content 构建完成：产物 {results.BundleInfos.Count} 个，" +
                  $"Object->Cluster 映射 {clusterOutput.ObjectToCluster.Count} 条，" +
                  $"Object->LocalID 映射 {clusterOutput.ObjectToLocalID.Count} 条");

        foreach (var kv in results.BundleInfos)
        {
            var file = SbpLabCommon.ResolveBundleFile(kv.Value);
            var size = System.IO.File.Exists(file) ? new System.IO.FileInfo(file).Length : 0;
            var deps = kv.Value.Dependencies == null ? 0 : kv.Value.Dependencies.Length;
            Debug.Log($"[Ex10]   archive={kv.Key} hash={kv.Value.Hash} size={SbpLabCommon.Human(size)} deps={deps}");
        }

        Debug.Log("[Ex10] 提示：这些文件名是 cluster hash，不是 bundle 名；" +
                  "运行时由 Addressables content catalog 负责映射");
    }
}
#endif
#endif
