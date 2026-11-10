// ---------------------------------------------------------------------------
// Example 19 —— FileType 探针：看看依赖关系里的 ObjectIdentifier 到底是什么
//
// 目的：
//   在 CalculateAssetDependencyData 之后，把每个 Asset 的
//   includedObjects / referencedObjects 的 ObjectIdentifier 打印出来：
//     - fileType（NonAssetType / SerializedAssetType / MetaAssetType / DeprecatedCachedAssetType）
//     - filePath、guid、localIdentifierInFile
//     - 主类型（ContentBuildInterface.GetTypeForObjects）
//   并统计 fileType 分布和 (fileType,filePath) 分布。
//
// 这样能直观看到：
//   - 普通 Assets/ 下的资源（prefab/material）是 SerializedAssetType；
//   - 内置 shader（Built-in Extra）是 NonAssetType，filePath = resources/unity_builtin_extra；
//   - library/unity default resources 的对象通常会被 Packing 过滤，不会进 bundle；
//   - MetaAssetType 在普通资源构建里很少出现（测试/内置 MonoScript 场景会用）。
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Injector;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEditor.Build.Pipeline.Tasks;
using UnityEngine;

public class FileTypeDumpTask : IBuildTask
{
    public int Version { get { return 1; } }

    [InjectContext(ContextUsage.In)] IDependencyData m_Dependency;
    [InjectContext(ContextUsage.In)] IBuildContent m_Content;

    public ReturnCode Run()
    {
        SbpLabEx19_FileTypeProbe.Dump(m_Dependency, m_Content);
        return ReturnCode.Success;
    }
}

public static class SbpLabEx19_FileTypeProbe
{
    [MenuItem("SBP Lab/Examples/19 - FileType 探针（依赖对象）", priority = 119)]
    public static void Run()
    {
        SbpLabCommon.EnsureLabAssets();

        // 只跑到 Dependency 结束，不需要写盘
        var all = DefaultBuildTasks.Create(DefaultBuildTasks.Preset.AssetBundleCompatible);
        var taskList = new List<IBuildTask>();
        foreach (var task in all)
        {
            taskList.Add(task);
            if (task is PostDependencyCallback)
                break;
        }
        taskList.Add(new FileTypeDumpTask());

        var output = SbpLabCommon.OutputFolder("ex19-filetypes");
        var parameters = new BundleBuildParameters(SbpLabCommon.Target, SbpLabCommon.Group, output)
        {
            BundleCompression = UnityEngine.BuildCompression.LZ4,
            UseCache = true,
        };
        var content = new BundleBuildContent(SbpLabCommon.ExplicitLayout());

        var code = ContentPipeline.BuildAssetBundles(parameters, content, out IBundleBuildResults _, taskList);
        Debug.Log("[Ex19] ReturnCode=" + code);
    }

    internal static void Dump(IDependencyData dependency, IBuildContent content)
    {
        var lines = new List<string>();
        var typeCount = new Dictionary<string, int>();
        var pathCount = new Dictionary<string, int>();

        lines.Add(string.Format("assets={0} scenes={1}", dependency.AssetInfo.Count, dependency.SceneInfo.Count));

        foreach (var kv in dependency.AssetInfo.OrderBy(x => AssetDatabase.GUIDToAssetPath(x.Key.ToString()), StringComparer.Ordinal))
        {
            var assetPath = AssetDatabase.GUIDToAssetPath(kv.Key.ToString());
            var info = kv.Value;
            lines.Add("");
            lines.Add("## " + assetPath);
            lines.Add("  includedObjects=" + info.includedObjects.Count + " referencedObjects=" + info.referencedObjects.Count);

            DumpObjects(lines, "  [included]", info.includedObjects, typeCount, pathCount);
            DumpObjects(lines, "  [referenced]", info.referencedObjects, typeCount, pathCount);
        }

        lines.Add("");
        lines.Add("## fileType 分布");
        foreach (var kv in typeCount.OrderBy(x => x.Key, StringComparer.Ordinal))
            lines.Add("  " + kv.Key + " = " + kv.Value);

        lines.Add("");
        lines.Add("## (fileType, filePath) 分布");
        foreach (var kv in pathCount.OrderBy(x => x.Key, StringComparer.Ordinal))
            lines.Add("  " + kv.Key + " = " + kv.Value);

        var text = string.Join("\n", lines.ToArray());
        Debug.Log("[Ex19]\n" + text);

        Directory.CreateDirectory(SbpLabCommon.ArtifactsRoot);
        File.WriteAllText(Path.Combine(SbpLabCommon.ArtifactsRoot, "ex19-filetypes.txt"), text);
    }

    static void DumpObjects(List<string> lines, string label, IEnumerable<ObjectIdentifier> objects,
        Dictionary<string, int> typeCount, Dictionary<string, int> pathCount)
    {
        if (objects == null)
            return;

        var objectArray = objects.ToArray();
        Type[] types = objectArray.Length > 0
            ? ContentBuildInterface.GetTypeForObjects(objectArray)
            : new Type[0];

        for (int i = 0; i < objectArray.Length; i++)
        {
            var obj = objectArray[i];
            var typeName = i < types.Length && types[i] != null ? types[i].Name : "?";
            var line = string.Format(
                "{0} fileType={1} type={2} path='{3}' guid={4} localId={5}",
                label, obj.fileType, typeName, obj.filePath, obj.guid, obj.localIdentifierInFile);
            lines.Add(line);

            var typeKey = obj.fileType + " / " + typeName;
            typeCount[typeKey] = (typeCount.ContainsKey(typeKey) ? typeCount[typeKey] : 0) + 1;

            var pathKey = obj.fileType + " / " + obj.filePath;
            pathCount[pathKey] = (pathCount.ContainsKey(pathKey) ? pathCount[pathKey] : 0) + 1;
        }
    }
}
#endif
