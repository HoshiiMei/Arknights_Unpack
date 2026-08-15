# ArkBrowser — 架构计划书

## 一、产品定义

**一句话**：拖入明日方舟加密 AB 文件 → 浏览 → 预览 → 导出，一个 exe 搞定。

**目标用户**：明日方舟同人创作者，Windows 环境，不需要装任何运行时。

---

## 二、WPF vs Avalonia：不只是"跨平台"

### 2.1 事实基础

| | AssetStudio | AssetRipper |
|---|---|---|
| **UI 框架** | WinForms | ASP.NET Core Web |
| **预览方式** | WinForms PictureBox + OpenTK | 浏览器 Canvas |
| **年代感** | 2018 | 2024（现代但"不像桌面应用"） |

两者都没用 Avalonia，也没有 WPF。说明在 Unity 解包工具这个品类里，**并没有收敛到某个"正确"的 UI 方案**。

### 2.2 对比

| 维度 | WPF | Avalonia |
|---|---|---|
| **平台** | Windows only | Win / Linux / macOS |
| **渲染引擎** | DirectX 9（硬件加速，旧但稳） | Skia（现代，性能好） |
| **样式系统** | XAML Style / Trigger，功能全但啰嗦 | CSS-like Fluent Style，更简洁 |
| **数据绑定** | `DependencyProperty` + `INotifyPropertyChanged`，成熟但模板代码多 | 同源但更现代，支持 `CompiledBindings`（性能更好） |
| **第三方控件** | 非常多（DevExpress、Telerik、Syncfusion） | 少，基本靠手写 |
| **社区/文档** | 20 年积累，stackoverflow 几乎全覆盖 | 较新，很多问题只能翻源码 |
| **Visual Studio 支持** | 一流，设计器 + 热重载 | 有设计器但不如 WPF 成熟 |
| **发布** | `win-x64` 单文件自包含 exe | 同上，但还可以发 Linux/macOS |
| **Trim/AOT** | 部分支持 | 支持 NativeAOT（更小的 exe） |

### 2.3 Avalonia 更"现代"体现在哪

WPF 的架构问题不是功能不够，而是**历史包袱**：

- `DependencyProperty` 要求类继承自 `DependencyObject`，侵入性强。Avalonia 的 `StyledProperty` 也是同一套思路但实现更干净。
- WPF 的 `BitmapSource` / `WriteableBitmap` 在处理大量纹理预览时内存管理比较坑，需要手动 `Freeze()`。Avalonia 用 Skia 的 `SKBitmap`，内存模型更可控。
- WPF 的自定义控件需要写 `ControlTemplate` + `Style`，Avalonia 可以直接用代码渲染（`CustomDrawing`），对于"图片预览框"这种自定义需求更友好。
- WPF 的多线程 UI 更新（`Dispatcher.Invoke`）在批量加载资产时容易出现卡顿。Avalonia 的渲染管线对高帧率场景做了优化。

### 2.4 但对你来说 WPF 可能更合适

理由很具体：

1. **你说 Avalonia "不成功"** — 我不知道具体卡在哪，但 WPF 的学习曲线确实更平缓，文档和教程多得多。
2. **目标用户 99% Windows** — 跨平台不是刚需。
3. **AssetStudio 是 WinForms，社区已经习惯了 Windows 原生工具** — 你的用户不会抱怨"为什么没有 macOS 版"。
4. **WPF 的 TreeView + Image + Flyout 组合对"资产浏览器"这个场景开箱即用**。
5. **发布也不成问题** — `.NET 8 + PublishSingleFile=true + self-contained` 就是一个 `ArkBrowser.exe`，用户不用装 .NET。

### 2.5 结论

**选 WPF**。不是因为它更先进，而是因为它在"做完"和"做好"之间给了最短路径。之后如果真想跨平台，核心逻辑（解密+解析+导出）全部在独立库中，换 Avalonia 壳只是 UI 层重写。

---

## 三、整体架构

```
┌──────────────────────────────────────────────────┐
│                 ArkBrowser.UI (WPF)               │
│  ┌──────────┐  ┌───────────┐  ┌───────────────┐  │
│  │ TreeView │  │  Preview  │  │  Export Panel │  │
│  │(资产导航)│  │  (预览区)  │  │  (导出面板)   │  │
│  └────┬─────┘  └─────┬─────┘  └───────┬───────┘  │
│       │              │               │           │
│  ┌────┴──────────────┴───────────────┴─────────┐  │
│  │              MainViewModel                  │  │
│  │    (MVVM: 状态、命令、资产列表、选中项)       │  │
│  └──────────────────────┬──────────────────────┘  │
├──────────────────────────┼─────────────────────────┤
│               ArkBrowser.Core                     │
│  ┌──────────────────────┼──────────────────────┐  │
│  │         AssetBrowserService                  │  │
│  │   ┌──────────────────┴──────────────────┐   │  │
│  │   │  IAssetSource                       │   │  │
│  │   │  ├── DecryptingAssetSource          │   │  │
│  │   │  └── PlainAssetSource              │   │  │
│  │   └──────────────────┬──────────────────┘   │  │
│  │   ┌──────────────────┴──────────────────┐   │  │
│  │   │  IPreviewGenerator                  │   │  │
│  │   │  ├── TexturePreviewGenerator        │   │  │
│  │   │  ├── AudioPreviewGenerator          │   │  │
│  │   │  └── MeshPreviewGenerator           │   │  │
│  │   └──────────────────┬──────────────────┘   │  │
│  │   ┌──────────────────┴──────────────────┐   │  │
│  │   │  IExportService                     │   │  │
│  │   │  └── AssetRipperExportAdapter       │   │  │
│  │   └─────────────────────────────────────┘   │  │
│  └─────────────────────────────────────────────┘  │
├──────────────────────────────────────────────────┤
│          External Dependencies                    │
│  ┌────────────────┐  ┌────────────────────────┐  │
│  │ K4os.Compress- │  │ AssetRipper (源码嵌入)  │  │
│  │ ion.LZ4 (NuGet)│  │ deps/AssetRipper/       │  │
│  └────────────────┘  └────────────────────────┘  │
└──────────────────────────────────────────────────┘
```

### 三层职责

| 层 | 项目 | 职责 | 对外暴露 |
|---|---|---|---|
| **UI** | `ArkBrowser.UI` (WPF) | 窗口、TreeView、Preview、ViewModel | — |
| **Core** | `ArkBrowser.Core` | 解密、资产管理、预览生成、导出编排 | `IAssetSource`, `IPreviewGenerator`, `IExportService` |
| **External** | K4os.Compression.LZ4 (NuGet) + AssetRipper 源码 | LZ4 解压、UnityFS 解析、纹理解码 | — |

Core 层**不引用任何 WPF 类型**（只产出 `byte[]` 和 `Stream`，UI 自己转成 `BitmapSource`），确保 UI 层可被替换。

---

## 四、管道设计（解密 → 解析 → 预览）

### 4.1 格式结构：Header 明文 + Block 加密

你在记事本里看到 `UnityFS` 签名是对的。**AB 文件头（Header）是明文存储的**，ArkLz4 只作用于内部的存储块（Storage Blocks），不是全文件加密。

```
AB 文件结构（ArkLz4 版本）：
┌──────────────────────────────────┐
│ Header (明文)                     │
│  "UnityFS" + version + flags...  │  ← 不加密，记事本能看到
├──────────────────────────────────┤
│ Blocks Info (ArkLz4 压缩)        │  ← 需要解密
├──────────────────────────────────┤
│ Nodes Info (ArkLz4 压缩)         │  ← 需要解密
├──────────────────────────────────┤
│ Data Blocks (可能含 ArkLz4)      │  ← 逐块判断 compression type
└──────────────────────────────────┘
```

这意味着解密流程**不是盲解全文件**，而是先读取 header，根据每个 block 的 flags 判断是否需要 ArkLz4 解压。这与技术报告中 `ConvertToUncompressed` 的逻辑一致——按块分派解压器。

### 4.2 整体数据流

```
拖入 AB 文件 / 文件夹
    │
    ▼
┌─────────────────────────────────────────┐
│ ① 解析 Header（明文，直接读）           │
│    signature, version, flags             │
│    compressedBlocksInfoSize              │
│    uncompressedBlocksInfoSize            │
└───────────────┬─────────────────────────┘
                │
                ▼
┌─────────────────────────────────────────┐
│ ② 读取 Blocks Info → 按需解密          │
│    根据 header.flags bit 0-5 决定:       │
│    - None(0)  → 直接读                  │
│    - Lz4(2)   → LZ4Codec.Decode         │
│    - ArkLz4(4)→ Nibble unpack → Swap    │
│                  → LZ4 Decode            │
└───────────────┬─────────────────────────┘
                │
                ▼
┌─────────────────────────────────────────┐
│ ③ 解压各 Data Block（逐块判断类型）    │
│    同样根据每个 block 的 flags 分派     │
│    → 解压后的数据直接注入 AssetRipper   │
│      的内部对象图，不重建完整文件        │
└───────────────┬─────────────────────────┘
                │
                ▼
┌─────────────────────────────────────────┐
│ ④ AssetRipper Import Pipeline           │
│    Block 数据 → SerializedFile → Assets │
│    产出：AssetCollection（对象树）       │
│    - Texture2D: 压缩像素 + 格式信息     │
│    - Sprite: 引用 Texture2D + rect      │
│    - AudioClip: 音频数据                │
│    - TextAsset: 文本/二进制             │
│    - Mesh: 顶点/面/UV                   │
└───────────────┬─────────────────────────┘
                │
                ▼
┌─────────────────────────────────────────┐
│ ⑤ 构建资产树                            │
│    按源文件组织：                        │
│    📁 char_pack/                        │
│    │  ├── 📁 building_textures/         │
│    │  │   ├── char_001_tex  [Texture2D] │
│    │  │   └── char_001_a    [Texture2D] │
│    │  ├── 📁 avg_sprites/               │
│    │  │   ├── avg_npc_01    [Sprite]    │
│    │  │   └── ...                       │
│    │  └── 📁 audio/                     │
│    │      └── voice_001     [AudioClip] │
│    📁 bg_pack/                          │
│    │  └── ...                           │
│    只存元数据，不加载像素                │
└───────────────┬─────────────────────────┘
                │
                ▼
┌─────────────────────────────────────────┐
│ ⑥ 后台批量生成缩略图（Background）     │
│    利用 Task.Run + 线程池                │
│    遍历所有 Texture2D → 解码 → 缩放到   │
│    256px → 写入 ThumbnailCache          │
│    进度条: "正在生成缩略图 45/120"      │
└───────────────┬─────────────────────────┘
                │
                ▼
┌─────────────────────────────────────────┐
│ ⑦ 用户浏览（即时响应）                  │
│    点击资产 → 优先取缓存缩略图           │
│    → 需要看大图时按空格 / 双击          │
│    → 解码全分辨率 → 预览面板展示         │
│    导出：选中 → IExportService          │
│    → PNG / WAV → 写入磁盘               │
└─────────────────────────────────────────┘
```

### 4.3 关键设计决策

#### 解密策略：拦截 AssetRipper 的块解压层，注入 ArkLz4

**不重建完整的未压缩 AB。** 那个思路多做了一次全量序列化/反序列化，纯浪费 CPU 和内存。

AssetRipper 内部解析 AB 的流程是：

```
BundleFile.Open(Stream)
  → ReadHeader()
  → ReadBlocksInfo()
  → 对每个 block：解压 → 得到 SerializedFile
```

我们的插入点就在"对每个 block 解压"这一步。AssetRipper 内部已有 LZ4 / LZMA / None 的分派逻辑，我们需要做的是**注册 ArkLz4 作为一种新的 block 解压方式**。

```
AssetRipper 解压分派（改造后）：
  block.compressionType switch {
      None   → 直接读
      Lzma   → LZMA Decode
      Lz4    → LZ4Codec.Decode
      Lz4HC  → LZ4Codec.Decode
      ArkLz4 → Nibble unpack → Byte-swap → LZ4Codec.Decode  ← 我们注入的
  }
```

这意味着我们**直接在 AssetRipper 源码中修改其 block 解压逻辑**，不是在外面包一层。数据流变成：

```
加密 AB 文件 → FileStream
  → AssetRipper BundleFile.Open(stream)
    → ReadHeader（明文，正常读）
    → ReadBlocksInfo（ArkLz4 压缩，走我们的解压器）
    → 每个 DataBlock（按需走 ArkLz4）
    → 产出 SerializedFile → AssetCollection
  → 无中间文件，无重建 AB
```

#### 内存模型

单文件流式读入，解压后的 block 直接驻留在 AssetRipper 的内部对象图中。不额外维护一份"标准 AB"的副本。内存占用约等于 `AssetCollection` 对象图的大小（纹理的压缩字节 + 元数据），不含解码后的 RGBA 像素。

#### 解析层 API 边界

```csharp
// Core 层封装
public interface IAssetSource
{
    AssetCollection Load(string filePath);      // 单个 AB
    AssetCollection LoadDirectory(string dir);  // 批量加载，合并视图
}
```

由于 ArkLz4 解压逻辑直接嵌入 AssetRipper 源码中，Core 层不暴露解密接口——它是 AssetRipper 内部的实现细节。对外部调用者来说，打开加密 AB 和打开标准 AB 的 API 完全一致。

---

## 五、预览机制：后台预生成 + 即时浏览

你要的是**能快速翻图找素材**，而不是"点一张等一张"。所以策略从纯懒加载改为**后台预生成缩略图**。

### 5.1 两阶段加载

```
阶段 1（加载 AB 时，同步）：
  ┌─ 解密 + 解析 → 构建资产树（元数据）──┤ 1-3 秒，树立即可用

阶段 2（后台，异步）：
  ┌─ 遍历资产树中的所有 Texture2D
  └─ 线程池并行解码 → 缩放到 256px → 写入缩略图缓存
     └─ 进度："正在生成预览 67/120"
     └─ 耗时：120 张纹理约 3-8 秒（取决于纹理压缩格式和大小）
```

用户不需要等阶段 2 完成——树已经在阶段 1 后可用，缩略图**一边生成一边出现在树旁边**（比如一个小的预览面板在选中时能看到已经生成的缩略图）。

### 5.2 缩放策略

缩略图的目的是**翻图时一眼辨认这是哪张立绘**，不需要全分辨率：

| 原图分辨率 | 缩略图尺寸 | 内存占用 |
|---|---|---|
| 2048×2048 | 256×256 | ~256KB（RGBA） |
| 1024×1024 | 256×256 | ~256KB |
| 4096×4096 | 512×384 | ~768KB |

**缩略图缓存全量保留**（所有已加载的 AB 文件中的纹理），因为 200 张缩略图也只占 ~50MB，完全在预算内。这保证你在树上随意点击任何资产，都是即时显示的——因为缩略图已经预生成了。

### 5.3 全分辨率预览（按需）

缩略图是给你在资产列表里快速翻的。当你看中某张图想确认细节：

```
双击 / 空格 → PreviewPanel 显示全分辨率
  ├── 检查 FullResCache
  │   ├── 命中 → 直接显示
  │   └── 未命中 → 异步解码原图 → 显示 → 缓存
  └── FullResCache 只保留最近 3 张（防止内存膨胀）
```

2048×2048 DXT5 纹理解码到 RGBA 约 16MB，一张足够看细节了。

### 5.4 缓存设计

```
┌─────────────────────────────────────────┐
│           ThumbnailCache                 │
│  key: asset.path                         │
│  value: WriteableBitmap (256px)         │
│  生命周期: 从 AB 加载到关闭              │
│  容量: 无上限，按需增长（~250KB/张）    │
│  策略: 全部保留                          │
│  目的: 翻图即时                          │
├─────────────────────────────────────────┤
│           FullResCache                   │
│  key: asset.path                         │
│  value: WriteableBitmap (原图大小)      │
│  生命周期: LRU，最近 3 张                │
│  策略: 最近使用的保留，旧的释放         │
│  目的: 细节查看                          │
├─────────────────────────────────────────┤
│           AudioCache                     │
│  key: asset.path                         │
│  value: byte[] (PCM WAV)                │
│  生命周期: 当前播放的 1 段               │
│  策略: 换曲释放                          │
└─────────────────────────────────────────┘
```

### 5.5 缩略图生成顺序

不是先来先服务，而是**按当前 TreeView 展开范围优先**：

1. 用户展开的文件夹 → 首先生成
2. 同一 AB 包内相邻的纹理 → 次之
3. 其他未展开的 → 最后

这样用户打开一个包，展开 `avg_sprites/`，那个文件夹里的图会最先出缩略图。

---

## 六、AssetRipper 集成方式

### 6.1 结论：源码嵌入，不是 NuGet 引用

AssetRipper 的核心库（`AssetRipper.Import`、`AssetRipper.Assets`、`AssetRipper.Export.*`）**不在 NuGet 上**。它们作为同一个 solution 的内部项目，靠 `ProjectReference` 互相引用，从未独立发布。

我们的方案：**将 AssetRipper 源码作为项目的一部分引入，直接在我们的 solution 中引用其项目文件。**

### 6.2 引入方式

```
ArkBrowser/
├── ArkBrowser.sln
├── src/
│   ├── ArkBrowser.Core/
│   ├── ArkBrowser.UI/
│   └── ...
├── deps/
│   └── AssetRipper/                    # git submodule
│       └── Source/
│           ├── AssetRipper.Assets/     ← 我们引用
│           ├── AssetRipper.Import/     ← 我们引用 + 修改（注入 ArkLz4）
│           ├── AssetRipper.Export/     ← 我们引用
│           ├── AssetRipper.Export.Modules.Textures/ ← 我们引用
│           ├── AssetRipper.Export.Modules.Audio/    ← 我们引用
│           ├── AssetRipper.IO.Files/   ← 我们引用
│           ├── AssetRipper.Numerics/   ← 我们引用
│           └── ...                     (其他项目按需引入)
```

### 6.3 我们需要修改 AssetRipper 的地方

| 位置 | 修改内容 | 侵入程度 |
|---|---|---|
| Block 解压分派逻辑 | 新增 `ArkLz4` case | 极小（加一个 switch case） |
| `ArkLz4Decryptor.cs` | 新增文件，放在 `ArkBrowser.Core/Decryption/` | AssetRipper 不感知 |

所有解压逻辑写在 ArkBrowser.Core 里，AssetRipper 只负责调用。这样 AssetRipper 上游更新时，冲突只出现在加了一行 case 的地方。

### 6.4 更新策略

AssetRipper 作为 git submodule 引入，锁定到某个稳定 commit。需要更新时：

```
cd deps/AssetRipper
git pull origin master
# 解决唯一的合并冲突：block 解压分派处的 ArkLz4 case
# 跑通构建
```

### 6.5 最小依赖集

不需要 AssetRipper 的全部项目。我们实际需要的：

| AssetRipper 项目 | 用途 |
|---|---|
| `AssetRipper.Assets` | 资产类型系统 |
| `AssetRipper.Import` | BundleFile / SerializedFile 解析（**需要修改**） |
| `AssetRipper.Export` | 导出管线基类 |
| `AssetRipper.Export.Modules.Textures` | 纹理数据导出 |
| `AssetRipper.Export.Modules.Audio` | 音频数据导出 |
| `AssetRipper.IO.Files` | 文件 IO 抽象 |
| `AssetRipper.Numerics` | 数学类型 |

不需要的项目（Cpp2IL、AssemblyDumper、GUI.*、Web.* 等）不引入 solution。

---

## 七、项目结构

```
ArkBrowser/
├── ArkBrowser.sln
├── src/
│   ├── ArkBrowser.Core/           # .NET 8 类库
│   │   ├── Decryption/
│   │   │   ├── ArkLz4Decryptor.cs  # nibble unpack + byte-swap + LZ4
│   │   │   ├── VarintReader.cs     # 变长整数读写
│   │   │   └── AbFormatDetector.cs # 检测是否为标准 AB
│   │   ├── AssetManagement/
│   │   │   ├── AssetBrowserService.cs  # 资产加载、索引、查询
│   │   │   └── AssetTreeBuilder.cs     # 构建树形结构
│   │   ├── Preview/
│   │   │   ├── IPreviewGenerator.cs
│   │   │   ├── TexturePreviewGenerator.cs
│   │   │   ├── AudioPreviewGenerator.cs
│   │   │   └── PreviewCache.cs
│   │   ├── Export/
│   │   │   ├── IExportService.cs
│   │   │   └── AssetRipperExportAdapter.cs
│   │   └── Extensions/
│   │       └── StreamExtensions.cs
│   │
│   ├── ArkBrowser.UI/             # .NET 8 WPF 应用
│   │   ├── ViewModels/
│   │   │   ├── MainViewModel.cs       # 主窗口状态
│   │   │   ├── AssetTreeViewModel.cs  # 资产树
│   │   │   ├── PreviewViewModel.cs    # 预览面板
│   │   │   └── ExportViewModel.cs     # 导出控制
│   │   ├── Views/
│   │   │   ├── MainWindow.xaml
│   │   │   ├── AssetTreeView.xaml
│   │   │   ├── PreviewPanel.xaml
│   │   │   └── ExportPanel.xaml
│   │   ├── Converters/
│   │   │   └── ByteToBitmapConverter.cs
│   │   └── App.xaml
│   │
│   └── ArkBrowser.UI.Tests/       # 单元测试（可选，先不做）
│
├── deps/                          # 本地依赖（git submodule）
│   └── AssetRipper/               # AssetRipper 源码
│
└── publish/
    └── publish.ps1                # 发布脚本：dotnet publish -r win-x64 --sc -p:PublishSingleFile=true
```

---

## 八、依赖与发布策略

### "不牵扯 NET8 依赖"的理解

我认为你真正的意思是：**不要像 ArkBundleConverter 那样，用户还需要装 .NET 运行时 + 命令行操作**。

解决方案：**.NET 自包含发布**。

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

结果：**一个 `ArkBrowser.exe`（约 70MB），双击即用，Windows 7/8/10/11 都行，不需要装任何东西。** .NET 8 运行时会被打包进 exe 里。

### 为什么不选 .NET Framework 4.8

- .NET Framework 4.8 是 Windows 自带的，exe 会小很多
- 但 AssetRipper 依赖的很多 NuGet 包（Cpp2IL, Tpk 等）**已经不支持 net472**
- 而且 .NET 8 的 JIT 性能（尤其是纹理解码这种密集计算场景）远好于 .NET Framework

结论：**.NET 8 + self-contained** 是唯一的务实选择。

---

## 九、ArkLz4 解密层

技术报告的核心逻辑不需要改，但我们对它的使用方式从"盲解全文件"变为"按块分派解压器"。

### 9.1 主流程

解密层并不独立运行，而是**嵌入 AssetRipper 的 block 解压分派逻辑中**。我们的代码只负责：给定压缩类型 `ArkLz4`（值为 4）+ 一段压缩字节 + 期望的未压缩大小 → 返回解压后的字节。

```csharp
// 这段代码在 AssetRipper 的 block 解压分派处被调用（我们加了一个 switch case）
// 不重建文件，不额外维护 AB 副本
internal static byte[] DecompressBlock(byte[] compressed, int compressedSize,
                                       int uncompressedSize, CompressionType type)
{
    return type switch
    {
        CompressionType.None   => compressed[..compressedSize],
        CompressionType.Lzma   => LzmaDecompress(compressed, compressedSize, uncompressedSize),
        CompressionType.Lz4     or
        CompressionType.Lz4HC   => LZ4Codec.Decode(compressed, 0, compressedSize,
                                                    new byte[uncompressedSize], 0, uncompressedSize),
        CompressionType.ArkLz4  => DecompressArkLz4(compressed, compressedSize, uncompressedSize),
        _ => throw new NotSupportedException($"Unknown compression type: {type}")
    };
}

// ArkLz4 = 预处理（nibble unpack + byte-swap） + 标准 LZ4
static byte[] DecompressArkLz4(byte[] compressed, int compressedSize, int uncompressedSize)
{
    // ... 见 9.2
}
```

### 9.2 ArkLz4 核心（不变）

```csharp
static byte[] DecompressArkLz4(byte[] compressed, int uncompressedSize)
{
    // Step 1: Nibble unpack
    var temp = ArrayPool<byte>.Shared.Rent(compressed.Length);
    Buffer.BlockCopy(compressed, 0, temp, 0, compressed.Length);
    int pos = 0;
    while (pos < compressed.Length)
    {
        byte b = temp[pos];
        int lower = b & 0x0F, upper = b >> 4;
        if (lower == 15) lower += ReadVarint(temp, ref pos);
        if (upper == 15) upper += ReadVarint(temp, ref pos);
        temp[pos] = (byte)((upper << 4) | (lower & 0x0F));
        pos++;
    }

    // Step 2: Byte-swap
    for (int i = 0; i < compressed.Length - 1; i += 2)
        (temp[i], temp[i + 1]) = (temp[i + 1], temp[i]);

    // Step 3: LZ4 decode
    var output = new byte[uncompressedSize];
    int written = LZ4Codec.Decode(temp, 0, compressed.Length, output, 0, uncompressedSize);
    ArrayPool<byte>.Shared.Return(temp);

    if (written != uncompressedSize)
        throw new InvalidDataException($"LZ4 decode error: wrote {written}, expected {uncompressedSize}");
    return output;
}

static int ReadVarint(byte[] data, ref int pos)
{
    int result = 0;
    while (pos < data.Length)
    {
        byte b = data[pos++];
        result += (b & 0x7F);
        if ((b & 0x80) == 0) return result;
        result <<= 7;
    }
    throw new EndOfStreamException();
}
```

### 9.3 算法验证策略

IL 反编译的算法逻辑可能有偏差。**不能靠猜测上线。**

#### 金标准方案

```
1. 拿到原始 ArkBundleConverter.exe（你已有的那个 .NET 8 程序）
2. 用它解一个已知的 Arknights AB 文件
   .\ArkBundleConverterCLI.exe uncompress -i encrypted.ab -o standard.ab
3. 输出文件 standard.ab → 这就是"金标准"（已知正确的未压缩 AB）
4. 你的解密层处理同一个 encrypted.ab
5. 逐字节对比你的输出和 standard.ab
6. 不一致 → 回到 IL 调试，直到完全一致
```

#### 分步验证

不一次性验证全流程，而是分段：

| 验证步骤 | 输入 | 期望输出 | 验证方式 |
|---|---|---|---|
| ReadVarint | 已知的 varint 编码字节序列 | 预期的整数值 | 单元测试 |
| Nibble unpack | 单个 ArkLz4 block | 还原后的字节 | 与金标准中间产物对比 |
| Byte-swap | Nibble 还原后的字节 | Swap 后字节 | 与金标准中间产物对比 |
| LZ4 decode | Swap 后的字节 | 最终解压数据 | `LZ4Codec.Decode` 返回值校验 |
| 端到端 | 完整加密 AB 文件 | 与金标准逐字节一致 | `Enumerable.SequenceEqual` |

#### 降级方案

如果 ArkLz4 算法短期内无法 100% 复现原版逻辑，一个保底路径：直接在你的程序里 **shell out 调用 ArkBundleConverter.exe** 做解密，解密后的标准 AB 再交给 AssetRipper。用户体验差一点（需要附带一个 exe），但能跑通全链路。

### 9.4 需要核对的地方

| 问题 | 说明 |
|---|---|
| ReadVarint 中的 `ref pos` 管理 | 原始 IL 中 varint 读取会推进 position，nibble unpack 的 pos 也随之移动。此处需要仔细对齐 |
| Header.flags 位掩码 | bit 6 (`BlocksAndDirectoryInfoCombined`) 影响 blocks + nodes 是否作为同一压缩块 |
| Byte-swap 的边界条件 | 原始代码中在 swap 之前还有一个 `upper==15` 的二次扩展检查，需要对照 IL 确认 |

---

## 十、开发阶段

| 阶段 | 内容 | 产出 |
|---|---|---|
| **P0：解密层** | `ArkLz4Decryptor`（header 明文读 + block 按需解压）+ 单元测试 | 能用已知 AB 文件验证解密正确性 |
| **P1：解析层集成** | 引入 AssetRipper 源码，在 block 解压分派处注入 ArkLz4 → `AssetCollection` | 内存中拿到完整资产对象 |
| **P2：最小 UI** | WPF 窗口 + TreeView（按源文件建树） | 看到所有 AB 里所有资产的列表 |
| **P3：后台缩略图** | 线程池并行解码 → 256px 缩略图 → `ThumbnailCache` + 进度条 | 翻图即时，无需等待 |
| **P4：预览面板** | 缩略图预览 + 双击全分辨率 + `FullResCache` LRU | 能快速翻找素材 + 确认细节 |
| **P5：导出** | 选中资产 → PNG / WAV 导出 | 核心功能闭环 |
| **P6：批量加载** | 打开目录 → 递归加载所有 AB → 合并逻辑树 → 所有资产可预览 | 一次打开整个 `Android/data/...` 目录 |
| **P7：打磨** | 音频试听、RGB+A 合并 toggle、多选导出、格式自动检测 | 完整可用 |

## 十一、已确定的事项

| 事项 | 决定 |
|---|---|
| **Spine / 骨骼动画** | **不做预览**，仅支持导出 |
| **背景** | 按普通 PNG 处理，不需要特殊逻辑 |
| **批量加载** | 根目录 → 所有 AB → 按源文件路径建逻辑树 → 全量资产可预览 |
| **RGB + Alpha** | 默认预览时自动合并显示；toggle 可切换单独看 RGB 通道 / Alpha 通道 |
| **本地化** | 先中文 |
| **开发环境** | Windows + Visual Studio 2022（Linux 上搞 WPF 是自虐） |
| **发布** | `dotnet publish -r win-x64 --sc -p:PublishSingleFile=true` → 一个 exe |
| **自动更新** | P7 之前不考虑 |

## 十二、待确认

1. **文件夹拖入 vs 文件选择器**：拖入操作更直观但实现稍复杂，要不要优先支持？
2. **音频预览方式**：双击播放还是内嵌一个迷你播放条？播放条可以先做简版（播放/停止 + 进度条）
3. **导出格式**：PNG 够了还是需要保留原始 DXT/ASTC？先做 PNG
4. **TypeTree / MonoBehaviour 数据**：只导出不预览？还是完全忽略？建议先忽略，用户群体不关心
