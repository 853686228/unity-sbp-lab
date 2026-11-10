// ---------------------------------------------------------------------------
// SBP Lab：重内容贴图生成器（用于把打包体积拉起来）
//
// 为什么需要它：
//   ARPGTutorial 里的大文件是 FBX（动画）和 WAV（音频），但这两类资源
//   **导入后序列化进 bundle 的体积远小于源文件**（动画/音频会被压缩），
//   所以只靠它们，bundle 只有 ~10 MB，压缩/缓存的差异不够明显。
//   贴图（尤其未压缩贴图）才是“打包体积”的大头，所以这里程序化生成
//   8 张 2048x2048 的未压缩贴图（约 100 MB packed），只用于本地实验。
//
// 说明：
//   - 贴图内容是可复现的渐变+色块图案，PNG 源文件很小，但导入设置改成
//     Uncompressed 后，打进 bundle 就是 RGB24/RGBA32 的大块数据；
//   - 仅用于本地学习实验，不要随仓库分发。
// ---------------------------------------------------------------------------
#if UNITY_EDITOR

using System.IO;
using UnityEditor;
using UnityEngine;

public static class SbpLabHeavySetup
{
    public const string TextureFolder = "Assets/Bundles/Heavy/Textures";
    public const int TextureCount = 8;
    public const int TextureSize = 2048;

    [MenuItem("SBP Lab/0b - Setup Heavy Textures（合成大贴图，约 100MB packed）", priority = 1)]
    public static void SetupTextures()
    {
        EnsureFolder("Assets/Bundles");
        EnsureFolder("Assets/Bundles/Heavy");
        EnsureFolder(TextureFolder);

        for (var i = 0; i < TextureCount; i++)
        {
            var path = string.Format("{0}/HeavyTex_{1:D2}.png", TextureFolder, i);
            if (!File.Exists(path))
                CreateTexture(path, i);

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                importer = AssetImporter.GetAtPath(path) as TextureImporter;
            }
            if (importer == null) continue;

            // 关键：未压缩 + 不生成 mipmap，保证进 bundle 的是大块原始像素
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = false;
            importer.maxTextureSize = TextureSize;
            importer.isReadable = false;
            importer.SaveAndReimport();
        }

        AssetDatabase.Refresh();
        Debug.Log($"[HeavySetup] 已准备 {TextureCount} 张 {TextureSize}x{TextureSize} 未压缩贴图 -> {TextureFolder}");
    }

    /// <summary>幂等入口：Ex08/Ex16 构建前调用，缺失就生成。</summary>
    public static void EnsureTextures()
    {
        if (!AssetDatabase.IsValidFolder(TextureFolder) ||
            Directory.GetFiles(TextureFolder, "HeavyTex_*.png").Length < TextureCount)
        {
            SetupTextures();
        }
    }

    static void CreateTexture(string path, int index)
    {
        var texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false);
        var pixels = new Color32[TextureSize * TextureSize];

        // 渐变 + 色块：内容可复现、PNG 压缩率高，但导入为未压缩后体积固定
        for (var y = 0; y < TextureSize; y++)
        {
            var g = (byte)(y * 255 / TextureSize);
            for (var x = 0; x < TextureSize; x++)
            {
                var r = (byte)(x * 255 / TextureSize);
                var b = (byte)((((x / 128) + (y / 128) + index) & 7) * 32);
                pixels[y * TextureSize + x] = new Color32(r, g, b, 255);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        File.WriteAllBytes(path, texture.EncodeToPNG());
        Object.DestroyImmediate(texture);

        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
    }

    static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        var parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        var name = Path.GetFileName(folder);
        if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, name);
    }
}
#endif
