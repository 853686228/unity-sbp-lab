// SBP Lab -- Scriptable Build Pipeline learning sandbox.
// Lives only in the throwaway E2E project under _e2e/.
//
// Menu items:
//   SBP Lab/0 - Setup Test Assets          create material + 3 prefabs, assign bundle names
//   SBP Lab/1 - Compatibility Build        drop-in replacement of BuildPipeline.BuildAssetBundles
//   SBP Lab/2 - ContentPipeline Build      explicit ContentPipeline build + report/diff
//   SBP Lab/3 - Touch CubeA                change one prefab (incremental build test)
//   SBP Lab/4 - Rebuild & Diff             rebuild and compare hashes with last report
//
// Batch (no GUI needed):
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod SbpLabBuild.BatchFullDemo -logFile -
#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEngine;
using Object = UnityEngine.Object;

[Serializable]
public class SbpBundleEntry
{
    public string name;
    public string file;
    public string hash;
    public uint crc;
    public long size;
    public int deps;
}

[Serializable]
public class SbpBuildReport
{
    public string target;
    public string builtAt;
    public List<SbpBundleEntry> bundles = new List<SbpBundleEntry>();
}

public static class SbpLabBuild
{
    const string BundleRoot = "Assets/Bundles";
    const string OutRoot = "Artifacts/SBP";
    const string ReportPath = OutRoot + "/last-report.json";

    // ---------------------------------------------------------------- 0. assets

    [MenuItem("SBP Lab/0 - Setup Test Assets", priority = 0)]
    public static void SetupTestAssets()
    {
        EnsureFolder("Assets", "Bundles");
        EnsureFolder(BundleRoot, "Shared");
        EnsureFolder(BundleRoot, "Props");

        var matPath = BundleRoot + "/Shared/Mat.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null)
        {
            var shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            mat = new Material(shader) { color = new Color(0.2f, 0.7f, 1f, 1f) };
            AssetDatabase.CreateAsset(mat, matPath);
        }

        CreateCube(BundleRoot + "/Props/CubeA.prefab", mat, new Vector3(-1.5f, 0.5f, 0f), Vector3.one);
        CreateCube(BundleRoot + "/Props/CubeB.prefab", mat, new Vector3(0f, 0.5f, 0f), Vector3.one);
        CreateCube(BundleRoot + "/Props/CubeC.prefab", mat, new Vector3(1.5f, 0.5f, 0f), Vector3.one);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        SetBundle(matPath, "shared");
        SetBundle(BundleRoot + "/Props/CubeA.prefab", "props_a");
        SetBundle(BundleRoot + "/Props/CubeB.prefab", "props_a");
        SetBundle(BundleRoot + "/Props/CubeC.prefab", "props_b");
        AssetDatabase.RemoveUnusedAssetBundleNames();

        Debug.Log("[SBP Lab] setup done: shared / props_a / props_b");
    }

    // ------------------------------------------------------------ 1. compat

    [MenuItem("SBP Lab/1 - Compatibility Build", priority = 1)]
    public static void CompatibilityBuild()
    {
        var target = EditorUserBuildSettings.activeBuildTarget;
        var outPath = Path.Combine(OutRoot, "compat-" + target);
        Directory.CreateDirectory(outPath);

        var manifest = CompatibilityBuildPipeline.BuildAssetBundles(
            outPath,
            BuildAssetBundleOptions.ChunkBasedCompression,
            target);

        Debug.Log(manifest == null
            ? "[SBP Lab] compatibility build FAILED -> " + outPath
            : "[SBP Lab] compatibility build OK -> " + outPath);
    }

    // ------------------------------------------------- 2. content pipeline + report

    [MenuItem("SBP Lab/2 - ContentPipeline Build", priority = 2)]
    public static void ContentPipelineBuild()
    {
        BuildAndReport();
    }

    public static SbpBuildReport BuildAndReport()
    {
        var target = EditorUserBuildSettings.activeBuildTarget;
        var group = BuildPipeline.GetBuildTargetGroup(target);
        var outPath = Path.Combine(OutRoot, "sbp-" + target);
        Directory.CreateDirectory(outPath);

        var builds = ContentBuildInterface.GenerateAssetBundleBuilds();
        if (builds == null || builds.Length == 0)
        {
            Debug.LogWarning("[SBP Lab] no assets carry an AssetBundle name. Run 'SBP Lab/0 - Setup Test Assets' first.");
            return null;
        }

        var content = new BundleBuildContent(builds);
        var buildParams = new BundleBuildParameters(target, group, outPath)
        {
            BundleCompression = UnityEngine.BuildCompression.LZ4,
            UseCache = true,
        };

        var code = ContentPipeline.BuildAssetBundles(
            buildParams, content, out IBundleBuildResults results);
        if (code != ReturnCode.Success)
        {
            Debug.LogError("[SBP Lab] ContentPipeline returned " + code);
            return null;
        }

        var report = new SbpBuildReport
        {
            target = target.ToString(),
            builtAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        foreach (var kv in results.BundleInfos)
        {
            var details = kv.Value;
            // BundleDetails.FileName is already relative to the project root (contains the output folder).
            var file = File.Exists(details.FileName)
                ? details.FileName
                : Path.Combine(outPath, details.FileName);
            report.bundles.Add(new SbpBundleEntry
            {
                name = kv.Key,
                file = file,
                hash = details.Hash.ToString(),
                crc = details.Crc,
                size = File.Exists(file) ? new FileInfo(file).Length : 0,
                deps = details.Dependencies == null ? 0 : details.Dependencies.Length,
            });
        }
        report.bundles.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

        var previous = LoadReport();
        foreach (var entry in report.bundles)
        {
            Debug.Log($"[SBP Lab] {entry.name,-24} hash={entry.hash} crc={entry.crc,-12} size={entry.size,8} deps={entry.deps}");
        }
        PrintDiff(previous, report);

        Directory.CreateDirectory(OutRoot);
        File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
        Debug.Log("[SBP Lab] report -> " + ReportPath + "  (output: " + outPath + ")");
        return report;
    }

    // ------------------------------------------------------- 3. touch one asset

    [MenuItem("SBP Lab/3 - Touch CubeA", priority = 3)]
    public static void TouchCubeA()
    {
        const string path = BundleRoot + "/Props/CubeA.prefab";
        if (!File.Exists(path))
        {
            Debug.LogWarning("[SBP Lab] CubeA.prefab not found. Run setup first.");
            return;
        }

        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            // Vary the scale so every run produces a real change (repeatable lab).
            var next = root.transform.localScale.x >= 1.5f ? 1.0f : root.transform.localScale.x + 0.1f;
            root.transform.localScale = new Vector3(next, next, next);
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Debug.Log("[SBP Lab] touched CubeA.prefab (localScale -> " + next.ToString("0.00") + ")");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        AssetDatabase.SaveAssets();
    }

    // ----------------------------------------------------------- 4. rebuild+diff

    [MenuItem("SBP Lab/4 - Rebuild & Diff", priority = 4)]
    public static void RebuildAndDiff()
    {
        BuildAndReport();
    }

    // ------------------------------------------------------ batch entry points

    public static void BatchSetupAndBuild()
    {
        RunBatch(() =>
        {
            SetupTestAssets();
            CompatibilityBuild();
            ContentPipelineBuild();
        });
    }

    public static void BatchFullDemo()
    {
        RunBatch(() =>
        {
            Debug.Log("===== SBP Lab: setup + first build =====");
            SetupTestAssets();
            CompatibilityBuild();
            ContentPipelineBuild();

            Debug.Log("===== SBP Lab: touch CubeA + incremental rebuild =====");
            TouchCubeA();
            ContentPipelineBuild();
        });
    }

    static void RunBatch(Action action)
    {
        try
        {
            action();
            Debug.Log("[SBP Lab] batch finished OK");
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    // ------------------------------------------------------------- helpers

    static void EnsureFolder(string parent, string name)
    {
        if (!AssetDatabase.IsValidFolder(parent + "/" + name))
            AssetDatabase.CreateFolder(parent, name);
    }

    static void CreateCube(string path, Material mat, Vector3 position, Vector3 scale)
    {
        if (File.Exists(path)) return;

        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = Path.GetFileNameWithoutExtension(path);
        go.transform.position = position;
        go.transform.localScale = scale;

        var renderer = go.GetComponent<Renderer>();
        if (renderer != null && mat != null) renderer.sharedMaterial = mat;

        PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);
    }

    static void SetBundle(string assetPath, string bundleName)
    {
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        var importer = AssetImporter.GetAtPath(assetPath);
        if (importer == null)
        {
            Debug.LogWarning("[SBP Lab] cannot set bundle name, importer missing: " + assetPath);
            return;
        }
        importer.assetBundleName = bundleName;
        importer.SaveAndReimport();
    }

    static SbpBuildReport LoadReport()
    {
        if (!File.Exists(ReportPath)) return null;
        try { return JsonUtility.FromJson<SbpBuildReport>(File.ReadAllText(ReportPath)); }
        catch { return null; }
    }

    static void PrintDiff(SbpBuildReport previous, SbpBuildReport current)
    {
        if (previous == null)
        {
            Debug.Log("[SBP Lab] diff: no previous report, this run is the baseline");
            return;
        }

        var oldMap = new Dictionary<string, SbpBundleEntry>();
        foreach (var b in previous.bundles) oldMap[b.name] = b;

        int changed = 0, same = 0;
        foreach (var now in current.bundles)
        {
            if (!oldMap.TryGetValue(now.name, out var before))
            {
                Debug.Log($"[SBP Lab] diff: + NEW      {now.name}");
                changed++;
            }
            else if (before.hash != now.hash)
            {
                Debug.Log($"[SBP Lab] diff: ~ CHANGED  {now.name}  {before.hash} -> {now.hash}  size {before.size} -> {now.size}");
                changed++;
            }
            else
            {
                same++;
            }
        }
        foreach (var before in previous.bundles)
        {
            if (current.bundles.Find(b => b.name == before.name) == null)
                Debug.Log($"[SBP Lab] diff: - REMOVED  {before.name}");
        }

        Debug.Log($"[SBP Lab] diff summary: changed={changed}, unchanged={same}, total={current.bundles.Count}");
    }
}
#endif
