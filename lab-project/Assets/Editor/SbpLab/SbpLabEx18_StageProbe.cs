// ---------------------------------------------------------------------------
// Example 18 —— 全流程阶段探针：在每个 Task 前后抓取 BuildContext 数据
//
// 目的（配合教学 Wiki "SBP 任务链逐阶段学习"）：
//   1. 按 AssetBundleCompatible 的 19 个任务顺序跑一次构建；
//   2. 在每个任务执行后插入一个探针任务，把 BuildContext 里的关键数据抓出来：
//        - 注册了哪些 IContextObject
//        - 内容布局（bundle -> asset 数）、GUID 数
//        - 依赖数据（asset/scene、included/referenced 对象数）
//        - 写盘数据（asset->file、file->bundle、WriteOperation 及序列化对象数）
//        - 结果（bundle hash/依赖/大小，或 WriteResults）
//        - 扩展数据（SubAsset/Sprite/显式对象布局）
//   3. 输出 Artifacts/SBP/ex18-stage-probe.json，供 Wiki 引用“每个阶段真实数据”。
//
// 原理：
//   BuildTasksRunner 会对 taskList 里每个任务做 Inject → Run → Extract。
//   探针本身也是 IBuildTask，字段上同样能用 [InjectContext] 拿到上下文，
//   因此只要把探针插在目标任务后面，就能看到该任务执行完的中间状态。
//
// 注意：探针任务会出现在 buildlogtep.json 里，属于学习工具，不要放进生产管线。
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Injector;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;
using UnityEngine.Build.Pipeline;

[Serializable]
public class Ex18Stage
{
    public string boundary;          // start / after XxxTask
    public string paramsInfo;
    public string contentInfo;
    public string dependencyInfo;
    public string writeDataInfo;
    public string resultsInfo;
    public string extrasInfo;
    public List<string> contextTypes = new List<string>();
}

[Serializable]
public class Ex18Report
{
    public string builtAt;
    public string platform;
    public string outputFolder;
    public int taskCount;
    public List<Ex18Stage> stages = new List<Ex18Stage>();
}

/// <summary>探针任务：只读取上下文并记录，不修改任何数据。</summary>
public class StageProbeTask : IBuildTask
{
    public int Version { get { return 1; } }

    readonly string m_Label;
    readonly string m_PreviousTaskName;
    readonly int m_PreviousTaskVersion;

    /// <param name="label">当前边界的名字，例如 "after GenerateBundlePacking"</param>
    /// <param name="previousTask">紧邻在探针前面的那个任务；传 null 表示构建开始（没有上一步）</param>
    public StageProbeTask(string label, IBuildTask previousTask = null)
    {
        m_Label = label;
        if (previousTask != null)
        {
            m_PreviousTaskName = previousTask.GetType().Name;
            m_PreviousTaskVersion = previousTask.Version;
        }
        else
        {
            m_PreviousTaskName = "（构建开始 / BuildContext 初始化）";
            m_PreviousTaskVersion = 0;
        }
    }

    [InjectContext(ContextUsage.In)]
    IBuildContext m_Context;

    [InjectContext(ContextUsage.In, true)] IBundleBuildParameters m_Params;
    [InjectContext(ContextUsage.In, true)] IBundleBuildContent m_Content;
    [InjectContext(ContextUsage.In, true)] IDependencyData m_Dependency;
    [InjectContext(ContextUsage.In, true)] IBundleWriteData m_WriteData;
    [InjectContext(ContextUsage.In, true)] IBuildResults m_Results;
    [InjectContext(ContextUsage.In, true)] IBundleBuildResults m_BundleResults;
    [InjectContext(ContextUsage.In, true)] IBuildExtendedAssetData m_ExtendedData;
    [InjectContext(ContextUsage.In, true)] IBuildSpriteData m_SpriteData;
    [InjectContext(ContextUsage.In, true)] IBundleExplictObjectLayout m_ObjectLayout;

    public ReturnCode Run()
    {
        Debug.Log(string.Format(
            "[StageProbe] 上一步骤 = {0}（v{1}） | 当前边界 = {2}",
            m_PreviousTaskName, m_PreviousTaskVersion, m_Label));

        SbpLabEx18_StageProbe.Capture(
            m_Label, m_Context, m_Params, m_Content, m_Dependency, m_WriteData,
            m_Results, m_BundleResults, m_ExtendedData, m_SpriteData, m_ObjectLayout);
        return ReturnCode.Success;
    }
}

public static class SbpLabEx18_StageProbe
{
    static readonly List<Ex18Stage> s_Stages = new List<Ex18Stage>();

    [MenuItem("SBP Lab/Examples/18 - 全流程阶段探针", priority = 118)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();
        s_Stages.Clear();

        // 默认 AssetBundleCompatible 预设：19 个任务
        var defaultTasks = DefaultBuildTasks.Create(DefaultBuildTasks.Preset.AssetBundleCompatible);
        var taskList = new List<IBuildTask>();
        taskList.Add(new StageProbeTask("start（BuildContext 初始化后）", null));
        foreach (var task in defaultTasks)
        {
            taskList.Add(task);
            // 把紧邻的前一个任务传进去，探针 Run() 就能打印“上一步骤”的名字
            taskList.Add(new StageProbeTask("after " + task.GetType().Name, task));
        }

        var outPath = SbpLabCommon.OutputFolder("ex18-stages");
        var parameters = new BundleBuildParameters(SbpLabCommon.Target, SbpLabCommon.Group, outPath)
        {
            BundleCompression = UnityEngine.BuildCompression.LZ4,
            UseCache = true,
        };
        var content = new BundleBuildContent(SbpLabCommon.ExplicitLayout());

        Debug.Log($"[Ex18] 开始：{defaultTasks.Count} 个默认任务 + {taskList.Count - defaultTasks.Count} 个探针");

        var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults results, taskList);
        if (code != ReturnCode.Success)
        {
            Debug.LogError("[Ex18] 构建失败，ReturnCode=" + code);
            return;
        }

        var report = new Ex18Report
        {
            builtAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            platform = SbpLabCommon.Target.ToString(),
            outputFolder = outPath,
            taskCount = defaultTasks.Count,
            stages = s_Stages,
        };

        Directory.CreateDirectory(SbpLabCommon.ArtifactsRoot);
        var jsonPath = Path.Combine(SbpLabCommon.ArtifactsRoot, "ex18-stage-probe.json");
        File.WriteAllText(jsonPath, JsonUtility.ToJson(report, true));
        Debug.Log($"[Ex18] 完成：{s_Stages.Count} 个阶段快照 -> {jsonPath}");
    }

    internal static void Capture(
        string label,
        IBuildContext context,
        IBundleBuildParameters parameters,
        IBundleBuildContent content,
        IDependencyData dependency,
        IBundleWriteData writeData,
        IBuildResults results,
        IBundleBuildResults bundleResults,
        IBuildExtendedAssetData extendedData,
        IBuildSpriteData spriteData,
        IBundleExplictObjectLayout objectLayout)
    {
        var stage = new Ex18Stage { boundary = label };

        // ---- BuildContext 里注册了哪些对象 ----
        if (context != null)
        {
            var field = context.GetType().GetField("m_ContextObjects", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null && field.GetValue(context) is IDictionary dict)
            {
                foreach (var key in dict.Keys)
                {
                    if (key is Type type) stage.contextTypes.Add(type.Name);
                    else stage.contextTypes.Add(key == null ? "(null)" : key.ToString());
                }
                stage.contextTypes.Sort(StringComparer.Ordinal);
            }
        }

        // ---- 参数 ----
        if (parameters != null)
        {
            var outputFolder = parameters.GetType().GetProperty("OutputFolder")?.GetValue(parameters) as string;
            // BundleCompression 只在具体类型 BundleBuildParameters 上，interface 没有，用反射读
            var compression = parameters.GetType().GetProperty("BundleCompression")?.GetValue(parameters);
            stage.paramsInfo = string.Format(
                "target={0} group={1} compression={2} appendHash={3} contiguous={4} useCache={5} output={6}",
                parameters.Target, parameters.Group, compression,
                parameters.AppendHash, parameters.ContiguousBundles, parameters.UseCache, outputFolder);
        }

        // ---- 内容布局 ----
        if (content != null)
        {
            var bundles = content.BundleLayout
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + "(" + kv.Value.Count + ")");
            stage.contentInfo = string.Format(
                "bundles=[{0}] assets={1} scenes={2} addresses={3} customAssets={4}",
                string.Join(", ", bundles.ToArray()), content.Assets.Count, content.Scenes.Count,
                content.Addresses.Count, content.CustomAssets.Count);
        }

        // ---- 依赖数据 ----
        if (dependency != null)
        {
            int included = 0, referenced = 0;
            foreach (var kv in dependency.AssetInfo)
            {
                included += kv.Value.includedObjects == null ? 0 : kv.Value.includedObjects.Count;
                referenced += kv.Value.referencedObjects == null ? 0 : kv.Value.referencedObjects.Count;
            }
            int sceneReferenced = 0;
            foreach (var kv in dependency.SceneInfo)
            {
                sceneReferenced += kv.Value.referencedObjects == null ? 0 : kv.Value.referencedObjects.Count;
            }
            var sample = dependency.AssetInfo.Take(3).Select(kv => string.Format(
                "{0}:{1}/{2}", SafeAssetName(kv.Key),
                kv.Value.includedObjects == null ? 0 : kv.Value.includedObjects.Count,
                kv.Value.referencedObjects == null ? 0 : kv.Value.referencedObjects.Count));

            stage.dependencyInfo = string.Format(
                "assets={0} scenes={1} assetObj={2}/{3} sceneReferenced={4} assetUsage={5} sample=[{6}]",
                dependency.AssetInfo.Count, dependency.SceneInfo.Count,
                included, referenced, sceneReferenced,
                dependency.AssetUsage.Count, string.Join(", ", sample.ToArray()));
        }

        // ---- 写盘数据 ----
        if (writeData != null)
        {
            var operations = writeData.WriteOperations
                .Select(op =>
                {
                    var internalName = op.Command == null ? "?" : op.Command.internalName;
                    string bundleName;
                    if (!writeData.FileToBundle.TryGetValue(internalName, out bundleName))
                        bundleName = Path.GetFileName(internalName);
                    var count = op.Command == null || op.Command.serializeObjects == null ? 0 : op.Command.serializeObjects.Count;
                    return string.Format("{0}:{1}({2})", bundleName, op.GetType().Name, count);
                });
            var assetToFiles = writeData.AssetToFiles
                .OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
                .Select(kv =>
                {
                    string bundleName;
                    if (!writeData.FileToBundle.TryGetValue(kv.Value[0], out bundleName))
                        bundleName = Path.GetFileName(kv.Value[0]);
                    return SafeAssetName(kv.Key) + "->" + bundleName;
                });
            stage.writeDataInfo = string.Format(
                "assetToFiles={0} files={1} bundles={2} writeOps={3}[{4}] usageSets={5} referenceMaps={6} assetMap=[{7}]",
                writeData.AssetToFiles.Count, writeData.FileToObjects.Count, writeData.FileToBundle.Count,
                writeData.WriteOperations.Count, string.Join(", ", operations.ToArray()),
                writeData.FileToUsageSet.Count, writeData.FileToReferenceMap.Count,
                string.Join(", ", assetToFiles.ToArray()));
        }

        // ---- 结果 ----
        // ---- 结果（同时展示写盘结果和最终 bundle 信息）----
        var resultParts = new List<string>();
        if (results != null)
        {
            resultParts.Add(string.Format("scriptTypeDB={0} writeResults={1} writeMeta={2}",
                results.ScriptResults.typeDB != null,
                results.WriteResults.Count, results.WriteResultsMetaData.Count));
        }
        if (bundleResults != null && bundleResults.BundleInfos.Count > 0)
        {
            var bundles = bundleResults.BundleInfos
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv =>
                {
                    var details = kv.Value;
                    var file = SbpLabCommon.ResolveBundleFile(details);
                    var size = File.Exists(file) ? new FileInfo(file).Length : 0;
                    return string.Format("{0}=hash:{1},size:{2}B,deps:{3}",
                        kv.Key, details.Hash, size,
                        details.Dependencies == null ? 0 : details.Dependencies.Length);
                });
            resultParts.Add(string.Format("bundleInfos={0} [{1}]",
                bundleResults.BundleInfos.Count, string.Join("; ", bundles.ToArray())));
        }
        stage.resultsInfo = string.Join(" | ", resultParts.ToArray());

        // ---- 扩展数据 ----
        var extras = new List<string>();
        if (extendedData != null)
        {
            var representations = extendedData.ExtendedData.Values.Sum(x => x.Representations == null ? 0 : x.Representations.Count);
            extras.Add(string.Format("extendedAssets={0} representations={1}", extendedData.ExtendedData.Count, representations));
        }
        if (spriteData != null)
        {
            extras.Add(string.Format("spriteImporter={0} packed={1}",
                spriteData.ImporterData.Count, spriteData.ImporterData.Values.Count(x => x.PackedSprite)));
        }
        if (objectLayout != null)
        {
            extras.Add(string.Format("explicitObjectLayout={0}",
                objectLayout.ExplicitObjectLocation == null ? 0 : objectLayout.ExplicitObjectLocation.Count));
        }
        stage.extrasInfo = string.Join("; ", extras.ToArray());

        s_Stages.Add(stage);

        Debug.Log(string.Format(
            "[Ex18] {0} | content[{1}] | dep[{2}] | write[{3}] | results[{4}] | extras[{5}] | ctx={6}",
            stage.boundary,
            stage.contentInfo, stage.dependencyInfo, stage.writeDataInfo,
            stage.resultsInfo, stage.extrasInfo, stage.contextTypes.Count));
    }

    static string SafeAssetName(GUID guid)
    {
        try
        {
            var path = AssetDatabase.GUIDToAssetPath(guid.ToString());
            if (!string.IsNullOrEmpty(path)) return Path.GetFileName(path);
        }
        catch { }
        return guid.ToString();
    }
}
#endif
