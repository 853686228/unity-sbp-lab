# YooAsset 3.0.7 加密与解密学习 Wiki

> 版本：YooAsset **3.0.7**（embedded package）
> 安装位置：`_e2e/UnityGradleDemo/Packages/com.tuyoogame.yooasset`
> 对照：Unity 2022.3.17f1c1 · SBP 1.23.4
>
> 相关文档：
> - [YooAsset-BuildCache.md](YooAsset-BuildCache.md) —— 构建缓存
> - [YooAsset-vs-SBP.md](YooAsset-vs-SBP.md) —— 构建管线对比
> - [SBP-BuildCache.md](SBP-BuildCache.md) —— SBP BuildCache

---

## 0. 结论摘要

YooAsset 3.0.7 的 Bundle 解密有 **3 种模式**：

| 模式 | 接口 | 运行时 API | 峰值内存 | 是否真正加密内容 |
|---|---|---|---|---|
| **Offset** | `IBundleOffsetDecryptor` | `AssetBundle.LoadFromFile(path, 0, offset)` | 几乎为 0（文件映射） | ❌ 通常只跳过头部/填充 |
| **Memory** | `IBundleMemoryDecryptor` | `AssetBundle.LoadFromMemory(byte[])` | 高（密文 + 明文 + Unity 副本） | ✅ |
| **Stream** | `IBundleStreamDecryptor` | `AssetBundle.LoadFromStream(stream, 0, bufferSize)` | 低（只占缓冲区） | ✅ |

一句话：

> **不是必须整包进内存。大 AB 用 Stream（最好是 LZ4 + AES-CTR），只有 WebGL/Archive/Raw 等特殊路径被迫用 Memory。**

---

## 1. 三种模式总览

### 1.1 Offset 模式（简单过一下）

**思路**：在真实 AB 前面加固定长度的头部/填充，运行时跳过。

```csharp
public class OffsetDecryptor : IBundleOffsetDecryptor
{
    long IBundleOffsetDecryptor.GetFileOffset(BundleDecryptArgs args)
    {
        return 32;
    }
}
```

运行时：

```csharp
AssetBundle.LoadFromFile(path, 0, offset);
```

- ✅ 内存最省，不读整包；
- ✅ 能避免 Unity 直接加载原始 AB；
- ❌ 正文通常是明文，**不是内容加密**；
- ❌ 对 AssetStudio/UnityPy 这类工具几乎不设防。

适合“头部混淆 + 低成本门槛”，不适合真正的资源保护。

### 1.2 Memory 模式（简单过一下）

**思路**：把整个密文读成 `byte[]`，解密成另一个 `byte[]`，再 `LoadFromMemory`。

```csharp
public class MemoryDecryptor : IBundleMemoryDecryptor
{
    byte[] IBundleMemoryDecryptor.GetDecryptedData(BundleDecryptArgs args)
    {
        byte[] data = args.FileData ?? FileUtility.ReadAllBytes(args.FilePath);
        Xor(data);
        return data;
    }
}
```

- ✅ 兼容性最好；
- ✅ 可以真正加密内容；
- ❌ 峰值内存高：密文 + 明文 + Unity 内部副本；
- ❌ 大 AB 容易造成卡顿/GC/内存峰值。

### 1.3 Stream 模式（本文重点）

**思路**：提供一个可 Seek 的解密流，Unity 按块读取并解密，不整包进 C# 内存。

```csharp
public class StreamDecryptor : IBundleStreamDecryptor
{
    Stream IBundleStreamDecryptor.CreateDecryptionStream(BundleDecryptArgs args)
    {
        return new AesCtrFileStream(args.FilePath, key, nonce);
    }

    int IBundleStreamDecryptor.GetBufferSize(BundleDecryptArgs args)
    {
        return 64 * 1024;
    }
}
```

运行时：

```csharp
AssetBundle.LoadFromStream(stream, 0, bufferSize);
```

- ✅ 内容真正加密；
- ✅ 内存只占 `bufferSize` + Unity 内部缓冲；
- ✅ 适合大 AB；
- ⚠️ 流必须可 Seek、可读、有 Length、能跨线程访问；
- ⚠️ LZMA 压缩仍会整包解压到内存（见 §6）。

---

## 2. Stream 接口与配置

### 2.1 接口定义

```csharp
public interface IBundleDecryptor
{
}

public interface IBundleStreamDecryptor : IBundleDecryptor
{
    int GetBufferSize(BundleDecryptArgs args);
    Stream CreateDecryptionStream(BundleDecryptArgs args);
}
```

`BundleDecryptArgs`：

```csharp
public readonly struct BundleDecryptArgs
{
    internal PackageBundle Bundle { get; }
    public byte[] FileData { get; }   // Stream 模式下为 null
    public string FilePath { get; }
}
```

> Stream 模式下 `FileData` 是 `null`，只能从 `FilePath` 打开文件；不要依赖 `FileData`。

### 2.2 运行时配置

```csharp
var fsParams = FileSystemParameters.CreateDefaultSandboxFileSystemParameters(
    remoteService, cacheRoot);

fsParams.AddParameter(
    EFileSystemParameter.AssetBundleDecryptor,
    new AesCtrStreamDecryptor(key, nonce));

// 可选：Android 极少数加载失败场景的后备解密器（Memory）
fsParams.AddParameter(
    EFileSystemParameter.AssetBundleFallbackDecryptor,
    new AesMemoryDecryptor(key, nonce));
```

注意：

- `AssetBundleDecryptor` 类型是 `IBundleDecryptor`；
- **一个类如果同时实现 Memory 和 Stream，YooAsset 会优先走 Memory**（选择顺序见下）；
- 想强制 Stream，就只实现 `IBundleStreamDecryptor`。

---

## 3. 加载链路

### 3.1 模式选择顺序

`LoadLocalAssetBundleOperation`：

```csharp
if (decryptor is IBundleOffsetDecryptor offsetDecryptor)
{
    result = LoadFromFile(offsetDecryptor);
}
else if (decryptor is IBundleMemoryDecryptor memoryDecryptor)
{
    result = LoadFromMemory(memoryDecryptor);
}
else if (decryptor is IBundleStreamDecryptor streamDecryptor)
{
    result = LoadFromStream(streamDecryptor);
}
else
{
    SetError($"does not support '{decryptor.GetType().Name}'.");
}
```

顺序：

```text
Offset → Memory → Stream
```

所以实现接口时不要“顺便”多实现一个 Memory，否则 Stream 永远轮不到。

### 3.2 Stream 分支源码

```csharp
private LoadResult LoadFromStream(IBundleStreamDecryptor decryptor)
{
    var args = new BundleDecryptArgs(_options.Bundle, null, _options.FilePath);

    uint bufferSize = (uint)decryptor.GetBufferSize(args);
    _loadStream = decryptor.CreateDecryptionStream(args);
    if (_loadStream == null)
        return LoadResult.Failure("decryptor returned null stream.");

    uint unityCRC = 0;
    if (IsWaitForCompletion)
        _assetBundle = AssetBundle.LoadFromStream(_loadStream, unityCRC, bufferSize);
    else
        _createRequest = AssetBundle.LoadFromStreamAsync(_loadStream, unityCRC, bufferSize);

    return LoadResult.Default();
}
```

要点：

- `GetBufferSize` 在 `CreateDecryptionStream` 之前调用；
- `unityCRC = 0`：YooAsset 不在这里做 Unity CRC 校验（缓存校验另有 `__info` CRC）；
- `LoadFromStream` / `LoadFromStreamAsync` 都会把流交给 Unity 读取；
- 流对象由 YooAsset 持有，失败时 `CleanupStream()`。

### 3.3 流的生命周期

```text
CreateDecryptionStream()
        ↓
AssetBundle.LoadFromStreamAsync()
        ↓
AssetBundleHandle(_assetBundle, _loadStream)
        ↓
Bundle 卸载：
    _assetBundle.Unload(true)
    _bundleStream.Dispose()
```

`AssetBundleHandle`：

```csharp
public void UnloadBundle()
{
    if (_assetBundle != null)
        _assetBundle.Unload(true);

    if (_bundleStream != null)
        _bundleStream.Dispose();
}
```

所以：

- **不能在 `LoadFromStreamAsync` 返回后立刻关闭流**；
- LZ4/未压缩 bundle 可能后续按需读取；
- 加载失败时 YooAsset 会调用 `CleanupStream()`；
- 自定义流必须允许被 Unity 在异步加载期间持续读取。

---

## 4. Unity 对 Stream 的硬性约束

Unity 官方文档（[LoadFromStream 2022.3](https://docs.unity.cn/cn/2022.3/ScriptReference/AssetBundle.LoadFromStream.html)）明确要求：

| 要求 | 说明 |
|---|---|
| **position 0 开始** | AssetBundle 数据必须从流位置 0 开始；Unity 会 Seek 到 0 |
| `CanRead == true` | 必须可读 |
| `CanSeek == true` | 必须可 Seek |
| `Length` | Unity 会读取长度 |
| 非主线程可访问 | Unity 可能从 native 线程调用 Read/Seek |
| 读取位置不被外部修改 | 否则数据会错乱 |

这意味着：

- `CryptoStream` 直接传进去 **不行**，因为 `CryptoStream.CanSeek == false`；
- 需要自己写一个可 Seek 的解密 Stream；
- 不要把“加密文件头 offset”混进 Stream 模式：数据必须从 0 开始；
- 需要 offset 就用 Offset 模式，不要用 Stream 模拟。

### 4.1 CRC 参数

Unity 的 `crc` 参数是“未压缩内容的 CRC-32”，非 0 时会校验。

YooAsset 传的是：

```csharp
uint unityCRC = 0;
```

所以：

- Unity 层不做 CRC 校验；
- YooAsset 的缓存文件在 `__info` 里另有 CRC（`dataFileCRC`），由缓存校验流程处理；
- 自定义加密流不需要在 `Read` 里再算 Unity CRC。

---

## 5. 自定义 Stream 解密器怎么写

### 5.1 推荐算法：AES-CTR / ChaCha20

| 算法 | 适合 Stream 吗 | 原因 |
|---|---|---|
| **AES-CTR** | ✅ | 无填充、长度不变、可随机 Seek |
| **ChaCha20** | ✅ | 同上，软件实现快 |
| AES-CBC | ⚠️ | 有 padding、Seek 要按 block 对齐并重置 IV |
| AES-GCM | ⚠️ | 认证标签在末尾，流式随机读不友好；需分块方案 |
| XOR | ❌（仅演示） | 不是加密 |

### 5.2 解密器骨架

```csharp
public sealed class AesCtrStreamDecryptor : IBundleStreamDecryptor
{
    private readonly byte[] _key;
    private readonly byte[] _nonce;

    public AesCtrStreamDecryptor(byte[] key, byte[] nonce)
    {
        _key = key;
        _nonce = nonce;
    }

    public int GetBufferSize(BundleDecryptArgs args)
    {
        return 64 * 1024;   // 64KB，别用示例里的 1KB
    }

    public Stream CreateDecryptionStream(BundleDecryptArgs args)
    {
        var file = new FileStream(
            args.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        return new AesCtrFileStream(file, _key, _nonce);
    }
}
```

### 5.3 `AesCtrFileStream` 必须实现什么

```csharp
internal sealed class AesCtrFileStream : Stream
{
    private readonly FileStream _inner;
    private readonly byte[] _key;
    private readonly byte[] _nonce;
    private long _position;

    public override bool CanRead  => true;
    public override bool CanSeek  => true;
    public override bool CanWrite => false;
    public override long Length   => _inner.Length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // 1. 根据 _position 计算 CTR block/counter
        // 2. 从 _inner 读密文
        // 3. AES 加密 counter，异或得到明文
        // 4. 更新 _position，返回读到的字节数
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        // 重新定位 _inner，并重算 CTR counter
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }
}
```

### 5.4 CTR 的 Seek 逻辑

AES-CTR 是流密码：

```text
keystream = AES(key, nonce || counter)
plaintext = ciphertext XOR keystream
```

任意位置读取：

```text
blockIndex = position / 16
blockOffset = position % 16
counter = nonce + blockIndex
```

所以 `Seek` 只需要：

1. 计算目标 block；
2. 重新生成该 block 的 keystream；
3. 从 `blockOffset` 开始异或；
4. 底层 `FileStream.Seek` 到对应密文位置。

这也是为什么 CTR 适合 Unity 的随机读取。

### 5.5 长度与填充

- AES-CTR / ChaCha20：密文长度 = 明文长度，`Length` 直接用文件长度；
- AES-CBC + PKCS7：密文比明文长，且 `Length` 应该返回明文长度；Unity 读 `Length` 时可能按密文长度处理，容易出错；
- 如果必须用 CBC，建议自己处理 padding，并让 `Read` 在末尾截断。

### 5.6 线程与资源

- Unity 可能从非主线程调用 `Read/Seek`；
- 不要在流里调用 Unity API；
- `FileStream` 的同步读取通常由 Unity 串行调用；如果担心并发，自己加锁；
- 使用 `FileShare.Read`，避免文件被占用；
- 不要在 `Read` 里分配大数组；复用 buffer；
- 不要在 `Read` 里抛异常“吞掉”，否则 Unity 会加载失败且难排查。

### 5.7 示例里的 XOR 流

YooAsset 示例：

```csharp
private class XorFileStream : FileStream
{
    public override int Read(byte[] array, int offset, int count)
    {
        int read = base.Read(array, offset, count);
        for (int i = offset; i < offset + read; i++)
            array[i] ^= XOR_KEY;
        return read;
    }
}
```

- 继承 `FileStream`，所以 `CanSeek/Length` 正常；
- XOR 与位置无关，Seek 天然正确；
- 但 **XOR 不是加密**，只用于演示接口；
- 生产环境请换成 AES-CTR/ChaCha20。

---

## 6. 压缩类型决定“省不省内存”

Unity 官方文档说明：

- **LZMA**：加载时**整个解压到内存**；
- **LZ4 / 未压缩**：直接从 Stream 按块读取。

所以：

| AB 压缩 | Stream 模式内存 | 建议 |
|---|---|---|
| **LZ4** | 只占 `bufferSize` + Unity 块缓存 | ✅ 大包首选 |
| **未压缩** | 按块读取，内存低 | ✅ 可选 |
| **LZMA** | 仍然整包解压到内存 | ⚠️ Stream 也救不了 |

如果目标是“大包低内存”：

```text
LZ4 压缩 + AES-CTR Stream 解密
```

LZMA 适合“下载包体小”，不适合“运行时内存低”。

---

## 7. 三种模式对比

| 维度 | Offset | Memory | Stream |
|---|---|---|---|
| 运行时 API | `LoadFromFile(path, 0, offset)` | `LoadFromMemory(byte[])` | `LoadFromStream(stream, 0, buffer)` |
| 峰值内存 | 极低（文件映射） | 高（密文+明文+副本） | 低（缓冲区） |
| 内容加密 | ❌ 通常只混淆头部 | ✅ | ✅ |
| AB 必须从 0 开始 | ❌ 可跳 offset | ✅ | ✅ |
| 需要可 Seek 流 | ❌ | ❌ | ✅ |
| 大 AB | ✅（但正文明文） | ❌ | ✅ |
| WebGL | ❌ | ✅ 唯一支持 | ❌ |
| Archive/Raw | ❌ | ✅ 唯一支持 | ❌ |
| 实现复杂度 | 低 | 低 | 中/高 |

---

## 8. 适用范围与限制

### 8.1 支持 Stream 的路径

- ✅ 本地普通 AssetBundle（Builtin/Sandbox 文件系统）；
- ✅ `AssetBundle.LoadFromStream` / `LoadFromStreamAsync`。

### 8.2 不支持 Stream 的路径

| 路径 | 支持的模式 | 原因 |
|---|---|---|
| **WebGL 加密下载** | 仅 Memory | `LoadWebEncryptedAssetBundleOperation` 只认 `IBundleMemoryDecryptor` |
| **ArchiveBundle 加密** | 仅 Memory | `LoadLocalArchiveBundleOperation` 只认 Memory |
| **RawBundle 加密** | 仅 Memory | `LoadLocalRawBundleOperation` 只认 Memory |
| **Android 后备加载** | 仅 Memory | `AssetBundleFallbackDecryptor` 类型是 `IBundleMemoryDecryptor` |

所以：

- 如果你要 WebGL + 加密，必须接受 Memory 的内存压力；
- 如果 Archive/Raw 加密，也必须整包进内存；
- 只有普通本地 AB 才能享受 Stream 的低内存。

### 8.3 Fallback 解密器

`SandboxBundleCache` 在加载失败（例如 Android 极少数真机问题）后会尝试后备加载：

```csharp
if (_bundle.IsEncrypted)
{
    if (config.AssetBundleFallbackDecryptor == null)
        return error;

    assetBundle = FallbackLoadEncryptedAssetBundle(config.AssetBundleFallbackDecryptor);
}
```

- `AssetBundleFallbackDecryptor` 是 `IBundleMemoryDecryptor`；
- 它是“兜底”，不是主路径；
- 不配置也能跑，但失败时没有后备。

---

## 9. 构建期加密

`IBundleEncryptor`：

```csharp
public interface IBundleEncryptor
{
    BundleEncryptResult Encrypt(BundleEncryptArgs args);
}

public readonly struct BundleEncryptResult
{
    public bool IsEncrypted { get; }
    public byte[] EncryptedFileData { get; }
}
```

`TaskEncryption`：

```csharp
var args = new BundleEncryptArgs(bundleInfo.BundleName, filePath);
var result = encryptionServices.Encrypt(args);
if (result.IsEncrypted)
{
    string encryptedFilePath = $"{pipelineOutputDirectory}/{bundleInfo.BundleName}.encrypt";
    WriteEncryptedFile(encryptedFilePath, result.EncryptedFileData);
    bundleInfo.EncryptedFilePath = encryptedFilePath;
    bundleInfo.Encrypted = true;
}
```

要点：

- 构建期接口返回 **完整 `byte[]`**，所以构建机加密也是整包读入内存；
- 大 AB 在构建机上也要注意内存；
- `TaskStableEncryption` 会对比已有加密文件，内容相同则不重写，利于增量构建；
- 加密后的文件路径会进入 manifest，运行时 `PackageBundle.IsEncrypted` 为 true，才走解密加载。

---

## 10. 性能与安全建议

### 10.1 性能

1. **用 LZ4，不要用 LZMA**：LZMA 会整包解压；
2. **bufferSize 64KB~1MB**：1KB 会导致大量 Read；
3. **AES-CTR 用硬件加速**：AES-NI；
4. **避免每次 Read 分配数组**；
5. **测量真实加载耗时**：Stream 解密有 CPU 开销，但内存可控；
6. **测试 Unload**：确认流被释放，没有文件句柄泄漏。

### 10.2 安全

- Stream 模式可以真正加密内容，但客户端密钥仍可能被逆向/内存 dump；
- 密钥不要明文硬编码在 `MonoBehaviour` 里；
- 可以放 native 插件、平台 keystore、远端下发；
- 接受“客户端加密不是绝对安全”的现实；
- 目标是提高提取成本，不是消灭提取。

---

## 11. 验证清单

- [ ] 加密后的 AB 用 AssetStudio/UnityPy 直接打开失败；
- [ ] 游戏内能正常加载；
- [ ] 大 AB 加载时内存峰值符合预期；
- [ ] LZ4 + Stream 组合验证；
- [ ] `Unload` 后文件句柄释放；
- [ ] Seek 后读取数据正确；
- [ ] 加载失败时流被清理；
- [ ] WebGL/Archive/Raw 路径确认只能 Memory；
- [ ] Android 后备加载路径验证（如果配置了 fallback）。

---

## 12. 源码索引

| 主题 | 文件 |
|---|---|
| 解密接口 | `Runtime/Interfaces/IBundleDecryptor.cs` |
| 加密接口 | `Runtime/Interfaces/IBundleEncryptor.cs` |
| 本地 AB 加载（三种模式） | `Runtime/BundleCache/Operations/Common/LoadLocalAssetBundleOperation.cs` |
| WebGL 加密加载（仅 Memory） | `Runtime/BundleCache/Operations/Common/LoadWebEncryptedAssetBundleOperation.cs` |
| Archive 加密加载（仅 Memory） | `Runtime/BundleCache/Operations/Common/LoadLocalArchiveBundleOperation.cs` |
| Raw 加密加载（仅 Memory） | `Runtime/BundleCache/Operations/Common/LoadLocalRawBundleOperation.cs` |
| 流生命周期 | `Runtime/BundleHandle/Services/AssetBundleHandle/AssetBundleHandle.cs` |
| 构建期加密任务 | `Editor/BundleBuilder/BuildTasks/TaskEncryption.cs` |
| 稳定加密（避免重写） | `Editor/BundleBuilder/BuildTasks/TaskStableEncryption.cs` |
| 示例 | `Samples~/Extension/Runtime/BundleEncryption/BundleEncryptionSample.cs` |

---

## 13. 一句话总结

> **Offset 是“跳过头部”，Memory 是“整包解密”，Stream 是“边读边解密”。大 AB 用 LZ4 + AES-CTR Stream，内存只占缓冲区；LZMA、WebGL、Archive、Raw 仍然会整包进内存。**

---

*最后更新：2026-11-14 · YooAsset 3.0.7 · Unity 2022.3.17f1c1*
