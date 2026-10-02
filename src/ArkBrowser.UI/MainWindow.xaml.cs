using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArkBrowser.Core.Decryption;
using ArkBrowser.Core.Serialization;
using Microsoft.Win32;

namespace ArkBrowser.UI;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ObjectRow> objects = [];
    private readonly List<LoadedBundle> bundles = [];

    private readonly ThumbnailService thumbnails = new();
    private readonly FullResCache fullResCache = new(capacity: 3);
    private int previewToken;
    private UnityFsReader? reader;
    private Stream? currentEntryStream;
    private ObjectRow? currentObjectRow;
    private Texture2DInfo? currentTexture;
    private string? currentObjectKey;
    private string? currentBundleKey;
    private int currentEntryIndex;
    private bool showingFullRes;
    private BitmapSource? previewBase;
    private ChannelView channelView = ChannelView.Rgba;

    public MainWindow()
    {
        InitializeComponent();

        ObjectsGrid.ItemsSource = objects;
        thumbnails.ProgressChanged += (done, total) => Dispatcher.BeginInvoke(() => UpdateThumbnailProgress(done, total));
        thumbnails.ThumbnailReady += (key, thumbnail) => Dispatcher.BeginInvoke(() => OnThumbnailReady(key, thumbnail));
        ChannelViewCombo.SelectedIndex = 0;

        UpdateFooter();
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "选择 Arknights AssetBundle",
            Filter = "AssetBundle (*.ab)|*.ab|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            OpenBundle(dialog.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.ToString(), "打开失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OpenDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "选择 Arknights AB 目录",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            OpenButton.IsEnabled = false;
            OpenDirectoryButton.IsEnabled = false;
            StatusText.Text = "正在扫描目录...";
            string rootPath = dialog.FolderName;
            (List<LoadedBundle> loaded, int skipped) = await Task.Run(() => LoadBundlesFromDirectory(rootPath));
            ApplyLoadedBundles(rootPath, loaded, skipped);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.ToString(), "打开目录失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            OpenButton.IsEnabled = true;
            OpenDirectoryButton.IsEnabled = true;
        }
    }

    private void OpenBundle(string path)
    {
        string rootPath = Path.GetDirectoryName(path) ?? path;
        List<LoadedBundle> loaded =
        [
            new LoadedBundle
            {
                FilePath = path,
                RelativePath = Path.GetFileName(path),
                Reader = UnityFsReader.Open(path),
            },
        ];

        ApplyLoadedBundles(rootPath, loaded);
    }

    private static (List<LoadedBundle> Loaded, int Skipped) LoadBundlesFromDirectory(string rootPath)
    {
        List<LoadedBundle> loaded = [];
        int skipped = 0;
        string[] supportedExtensions = [".ab", ".unity3d"];
        foreach (string file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            if (!supportedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                loaded.Add(new LoadedBundle
                {
                    FilePath = file,
                    RelativePath = Path.GetRelativePath(rootPath, file),
                    Reader = UnityFsReader.Open(file),
                });
            }
            catch
            {
                skipped++;
            }
        }

        loaded.Sort((left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));
        return (loaded, skipped);
    }

    private void ApplyLoadedBundles(string rootPath, IReadOnlyList<LoadedBundle> loaded, int skipped = 0)
    {
        ClearAllBundles();
        bundles.AddRange(loaded);

        PathTextBox.Text = rootPath;
        StatusText.Text = string.Empty;
        DetailTextBox.Text = string.Empty;
        SetPreview(null);
        ResetThumbnailProgress();
        BuildAssetTree(rootPath, loaded);

        StatusText.Text = skipped > 0
            ? $"已加载 {loaded.Count} 个 AB，跳过 {skipped} 个无效文件"
            : $"已加载 {loaded.Count} 个 AB";
        UpdateFooter();
    }

    private void ClearAllBundles()
    {
        ResetCurrentEntry();
        foreach (LoadedBundle bundle in bundles)
        {
            bundle.Reader.Dispose();
        }

        bundles.Clear();
        objects.Clear();
        thumbnails.Clear();
        fullResCache.Clear();
        AssetTree.Items.Clear();
        reader = null;
        SetPreview(null);
        DetailTextBox.Text = string.Empty;
        ResetThumbnailProgress();
    }

    private void BuildAssetTree(string rootPath, IReadOnlyList<LoadedBundle> loaded)
    {
        AssetTree.Items.Clear();
        string rootName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(rootName))
        {
            rootName = rootPath;
        }

        TreeViewItem rootItem = new() { Header = rootName };
        foreach (LoadedBundle bundle in loaded)
        {
            rootItem.Items.Add(CreateBundleItem(bundle));
        }

        AssetTree.Items.Add(rootItem);
        rootItem.IsExpanded = true;
    }

    private static TreeViewItem CreateBundleItem(LoadedBundle bundle)
    {
        TreeViewItem bundleItem = new()
        {
            Header = bundle.RelativePath,
            Tag = new BundleNode(bundle),
        };

        for (int i = 0; i < bundle.Reader.Entries.Count; i++)
        {
            UnityFsEntry entry = bundle.Reader.Entries[i];
            bundleItem.Items.Add(new TreeViewItem
            {
                Header = entry.Path,
                Tag = new EntryNode(bundle, i),
            });
        }

        return bundleItem;
    }

    private void AssetTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (AssetTree.SelectedItem is not TreeViewItem { Tag: EntryNode entryNode })
        {
            ClearObjects();
            return;
        }

        try
        {
            LoadEntryNode(entryNode);
        }
        catch (Exception exception)
        {
            ClearObjects();
            StatusText.Text = $"解析失败：{exception.Message}";
            UpdateFooter();
        }
    }

    private void LoadEntryNode(EntryNode entryNode)
    {
        ResetCurrentEntry();
        currentBundleKey = entryNode.Bundle.RelativePath;
        currentEntryIndex = entryNode.EntryIndex;
        objects.Clear();
        DetailTextBox.Text = string.Empty;
        SetPreview(null);

        UnityFsEntry unityEntry = entryNode.Bundle.Reader.Entries[entryNode.EntryIndex];
        if (!unityEntry.IsSerializedFile)
        {
            StatusText.Text = $"{unityEntry.Path} 是资源流，跳过 SerializedFile 对象表解析。";
            UpdateFooter();
            return;
        }

        reader = entryNode.Bundle.Reader;
        currentEntryStream = reader.OpenEntry(entryNode.EntryIndex);
        SerializedFileInfo serializedFile = SerializedFileParser.Parse(currentEntryStream);

        foreach (SerializedObjectInfo objectInfo in serializedFile.Objects)
        {
            objects.Add(new ObjectRow(
                objectInfo.PathId,
                objectInfo.TypeId,
                objectInfo.ByteStart,
                objectInfo.ByteSize,
                objectInfo.SerializedTypeIndex,
                objectInfo.ScriptTypeIndex,
                objectInfo.IsStripped,
                objectInfo));
        }

        ObjectRow? firstTexture = objects.FirstOrDefault(o => o.TypeId == 28);
        ObjectsGrid.SelectedItem = firstTexture;

        int entryIndex = entryNode.EntryIndex;
        UnityFsReader capturedReader = entryNode.Bundle.Reader;
        List<ThumbnailRequest> thumbnailRequests = [];
        foreach (ObjectRow objectRow in objects)
        {
            if (objectRow.TypeId != 28)
            {
                continue;
            }

            ObjectRow capturedRow = objectRow;
            thumbnailRequests.Add(new ThumbnailRequest(
                MakeThumbnailKey(capturedRow),
                () => LoadThumbnailInput(capturedReader, entryIndex, capturedRow)));
        }

        thumbnails.Enqueue(thumbnailRequests);

        StatusText.Text =
            $"{entryNode.Bundle.RelativePath} → {unityEntry.Path}  Types={serializedFile.Types.Count}  Objects={serializedFile.Objects.Count}  DataOffset={serializedFile.Header.DataOffset}";
        UpdateFooter();
    }

    private void ObjectsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DetailTextBox.Text = string.Empty;
        SetPreview(null);
        currentObjectRow = null;
        currentTexture = null;
        currentObjectKey = null;
        showingFullRes = false;

        if (ObjectsGrid.SelectedItem is not ObjectRow row)
        {
            return;
        }

        if (reader is null || currentEntryStream is null)
        {
            return;
        }

        if (row.TypeId == 83)
        {
            DetailTextBox.Text = "AudioClip：音频试听/导出需接入 FMOD/Unity Audio 解码，当前版本暂未支持。";
            return;
        }

        if (row.TypeId != 28)
        {
            DetailTextBox.Text = $"当前对象 TypeID={row.TypeId}，暂未接入专项解析。";
            return;
        }

        try
        {
            using Stream objectStream = SerializedFileParser.OpenObject(currentEntryStream, row.Info);
            Texture2DInfo texture = Texture2DReader.Read(objectStream);
            currentObjectRow = row;
            currentTexture = texture;
            currentObjectKey = MakeThumbnailKey(row);
            showingFullRes = false;

            StringBuilder detail = new();
            detail.AppendLine($"Name: {texture.Name}");
            detail.AppendLine($"Width: {texture.Width}");
            detail.AppendLine($"Height: {texture.Height}");
            detail.AppendLine($"TextureFormat: {texture.TextureFormat}");
            detail.AppendLine($"CompleteImageSize: {texture.CompleteImageSize}");
            detail.AppendLine($"InlineImageSize: {texture.InlineImageSize}");

            if (texture.StreamingInfo is not null)
            {
                detail.AppendLine($"StreamingInfo.Offset: {texture.StreamingInfo.Offset}");
                detail.AppendLine($"StreamingInfo.Size: {texture.StreamingInfo.Size}");
                detail.AppendLine($"StreamingInfo.Path: {texture.StreamingInfo.Path}");

                using Stream? resourceStream = new BundleResourceResolver(reader).OpenStream(texture.StreamingInfo);
                if (resourceStream is null)
                {
                    detail.AppendLine(".resS: no matching resource entry.");
                }
                else
                {
                    int headLength = (int)Math.Min(64, resourceStream.Length);
                    byte[] head = new byte[headLength];
                    int read = resourceStream.Read(head, 0, headLength);
                    detail.AppendLine($".resS slice length: {resourceStream.Length}");
                    detail.AppendLine($".resS head ({read} bytes): {Convert.ToHexString(head, 0, read)}");
                }
            }
            else
            {
                detail.AppendLine("StreamingInfo: (null / inline)");
            }

            DetailTextBox.Text = detail.ToString();
            ShowThumbnail(row);
        }
        catch (Exception exception)
        {
            DetailTextBox.Text = $"解析 Texture2D 失败：{exception.Message}";
        }
    }

    private void ObjectsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ObjectsGrid.SelectedItem is ObjectRow { TypeId: 28 })
        {
            LoadFullResAsync();
        }
    }

    private void FullResButton_Click(object sender, RoutedEventArgs e)
    {
        LoadFullResAsync();
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (reader is null || currentEntryStream is null)
        {
            MessageBox.Show(this, "请先在 Objects 列表中选择一个资源。", "导出", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        List<ObjectRow> selected = ObjectsGrid.SelectedItems.OfType<ObjectRow>().Where(o => o.TypeId == 28).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请先在 Objects 列表中选择一个 Texture2D。", "导出", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (selected.Count == 1)
        {
            await ExportSingleTextureAsync(selected[0]);
            return;
        }

        OpenFolderDialog folderDialog = new()
        {
            Title = "选择导出目录",
        };

        if (folderDialog.ShowDialog(this) != true)
        {
            return;
        }

        await ExportTexturesToFolderAsync(selected, folderDialog.FolderName);
    }

    private async Task ExportSingleTextureAsync(ObjectRow row)
    {
        if (reader is null || currentEntryStream is null)
        {
            return;
        }

        byte[]? compressed;
        Texture2DInfo texture;
        try
        {
            compressed = PrepareTexture(row, out texture);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"读取像素数据失败：{exception.Message}", "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (compressed is null)
        {
            MessageBox.Show(this, "该纹理没有可导出的像素数据。", "导出", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string safeName = MakeSafeFileName(texture.Name, "texture");
        SaveFileDialog dialog = new()
        {
            Title = "导出 PNG",
            Filter = "PNG 图片 (*.png)|*.png",
            FileName = $"{safeName}.png",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string targetPath = dialog.FileName;
        StatusText.Text = "正在导出 PNG...";
        try
        {
            await Task.Run(() => SaveTexturePng(texture, compressed, targetPath));
            StatusText.Text = $"已导出：{targetPath}";
        }
        catch (Exception exception)
        {
            StatusText.Text = string.Empty;
            MessageBox.Show(this, $"导出失败：{exception.Message}", "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ExportTexturesToFolderAsync(IReadOnlyList<ObjectRow> rows, string outputDirectory)
    {
        List<(ObjectRow Row, Texture2DInfo Texture, byte[] Compressed)> jobs = [];
        int readFailures = 0;
        foreach (ObjectRow row in rows)
        {
            try
            {
                byte[]? compressed = PrepareTexture(row, out Texture2DInfo texture);
                if (compressed is null)
                {
                    readFailures++;
                    continue;
                }

                jobs.Add((row, texture, compressed));
            }
            catch
            {
                readFailures++;
            }
        }

        if (jobs.Count == 0)
        {
            MessageBox.Show(this, "没有可导出的纹理。", "导出", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StatusText.Text = $"正在导出 {jobs.Count} 张 PNG...";
        int exported = 0;
        List<string> errors = [];
        await Task.Run(() =>
        {
            foreach ((ObjectRow row, Texture2DInfo texture, byte[] compressed) in jobs)
            {
                string fileName = MakeSafeFileName(texture.Name, "texture");
                string targetPath = BuildUniquePath(outputDirectory, fileName, row.Info.PathId);
                try
                {
                    SaveTexturePng(texture, compressed, targetPath);
                    Interlocked.Increment(ref exported);
                }
                catch (Exception exception)
                {
                    lock (errors)
                    {
                        errors.Add($"{fileName}: {exception.Message}");
                    }
                }
            }
        });

        StatusText.Text = $"已导出 {exported}/{jobs.Count} 张 PNG" + (readFailures > 0 ? $"，读取失败 {readFailures}" : string.Empty);
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, errors.Take(5)), "部分导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private byte[]? PrepareTexture(ObjectRow row, out Texture2DInfo texture)
    {
        if (reader is null || currentEntryStream is null)
        {
            texture = null!;
            return null;
        }

        using Stream objectStream = SerializedFileParser.OpenObject(currentEntryStream, row.Info);
        texture = Texture2DReader.Read(objectStream);
        return ReadPixelData(reader, currentEntryStream, row, texture);
    }

    private static void SaveTexturePng(Texture2DInfo texture, byte[] compressed, string targetPath)
    {
        byte[] rgba = Texture2DDecoder.DecodeToRgba32(texture, compressed);
        BitmapSource? bitmap = CreateBitmapSource(rgba, texture.Width, texture.Height);
        if (bitmap is null)
        {
            throw new InvalidOperationException("无法生成位图。");
        }

        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(targetPath);
        encoder.Save(output);
    }

    private static string BuildUniquePath(string outputDirectory, string fileName, long pathId)
    {
        string firstCandidate = Path.Combine(outputDirectory, $"{fileName}.png");
        if (!File.Exists(firstCandidate))
        {
            return firstCandidate;
        }

        return Path.Combine(outputDirectory, $"{fileName}_{pathId}.png");
    }

    private static string MakeSafeFileName(string name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        string sanitized = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    private void ShowThumbnail(ObjectRow row)
    {
        string key = MakeThumbnailKey(row);
        if (thumbnails.TryGet(key, out BitmapSource? thumbnail))
        {
            SetPreview(thumbnail);
            return;
        }

        SetPreview(null);
    }

    private void OnThumbnailReady(string key, BitmapSource thumbnail)
    {
        if (showingFullRes || currentObjectKey != key)
        {
            return;
        }

        SetPreview(thumbnail);
    }

    private void ChannelViewCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelViewCombo.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        channelView = tag switch
        {
            "Rgb" => ChannelView.Rgb,
            "Alpha" => ChannelView.Alpha,
            _ => ChannelView.Rgba,
        };

        ApplyChannelView();
    }

    private void SetPreview(BitmapSource? source)
    {
        previewBase = source;
        ApplyChannelView();
    }

    private void ApplyChannelView()
    {
        if (previewBase is null)
        {
            PreviewImage.Source = null;
            return;
        }

        PreviewImage.Source = channelView == ChannelView.Rgba
            ? previewBase
            : RenderChannelView(previewBase, channelView);
    }

    private static BitmapSource RenderChannelView(BitmapSource source, ChannelView view)
    {
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        if (view == ChannelView.Rgb)
        {
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
        }
        else if (view == ChannelView.Alpha)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte alpha = pixels[i + 3];
                pixels[i] = alpha;
                pixels[i + 1] = alpha;
                pixels[i + 2] = alpha;
                pixels[i + 3] = 255;
            }
        }

        BitmapSource rendered = BitmapSource.Create(width, height, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        rendered.Freeze();
        return rendered;
    }

    private async void LoadFullResAsync()
    {
        if (currentObjectRow is null || currentTexture is null || reader is null || currentEntryStream is null)
        {
            return;
        }

        ObjectRow row = currentObjectRow;
        Texture2DInfo texture = currentTexture;
        string key = MakeThumbnailKey(row);
        if (fullResCache.TryGet(key, out BitmapSource? cached))
        {
            showingFullRes = true;
            SetPreview(cached);
            return;
        }

        byte[]? compressed;
        try
        {
            compressed = ReadPixelData(reader, currentEntryStream, row, texture);
        }
        catch (Exception exception)
        {
            DetailTextBox.Text = $"读取像素数据失败：{exception.Message}";
            return;
        }

        if (compressed is null)
        {
            return;
        }

        int token = ++previewToken;
        StatusText.Text = "正在加载原图...";
        BitmapSource? bitmap = await Task.Run(() =>
        {
            try
            {
                byte[] rgba = Texture2DDecoder.DecodeToRgba32(texture, compressed);
                return CreateBitmapSource(rgba, texture.Width, texture.Height);
            }
            catch
            {
                return null;
            }
        });

        if (token != previewToken)
        {
            return;
        }

        if (bitmap is not null)
        {
            fullResCache.Add(key, bitmap);
            showingFullRes = true;
            SetPreview(bitmap);
            StatusText.Text = $"原图 {texture.Width}x{texture.Height}";
        }
    }

    private static byte[]? ReadPixelData(UnityFsReader reader, Stream entryStream, ObjectRow row, Texture2DInfo texture)
    {
        if (texture.StreamingInfo is not null && texture.StreamingInfo.IsSet)
        {
            using Stream? resourceStream = new BundleResourceResolver(reader).OpenStream(texture.StreamingInfo);
            if (resourceStream is null)
            {
                return null;
            }

            byte[] compressed = new byte[checked((int)texture.StreamingInfo.Size)];
            resourceStream.ReadExactly(compressed);
            return compressed;
        }

        if (texture.InlineImageSize > 0)
        {
            using Stream objectStream = SerializedFileParser.OpenObject(entryStream, row.Info);
            byte[] compressed = new byte[texture.InlineImageSize];
            objectStream.Position = texture.InlineImageOffset;
            objectStream.ReadExactly(compressed);
            return compressed;
        }

        return null;
    }

    private static ThumbnailJobInput? LoadThumbnailInput(UnityFsReader reader, int entryIndex, ObjectRow row)
    {
        try
        {
            using Stream entryStream = reader.OpenEntry(entryIndex);
            using Stream objectStream = SerializedFileParser.OpenObject(entryStream, row.Info);
            Texture2DInfo texture = Texture2DReader.Read(objectStream);
            byte[]? compressed = ReadPixelData(reader, entryStream, row, texture);
            return compressed is null ? null : new ThumbnailJobInput(texture, compressed);
        }
        catch
        {
            return null;
        }
    }

    private string MakeThumbnailKey(ObjectRow row)
    {
        return $"{currentBundleKey ?? string.Empty}:{currentEntryIndex}:{row.Info.PathId}:{row.Info.ByteStart}";
    }
    internal static BitmapSource? CreateBitmapSource(byte[] rgba, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        int stride = checked(width * 4);
        if (rgba.Length < stride * height)
        {
            return null;
        }

        byte[] bgra = new byte[stride * height];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = rgba[i + 3];
        }

        byte[] flipped = new byte[bgra.Length];
        for (int y = 0; y < height; y++)
        {
            Array.Copy(bgra, y * stride, flipped, (height - 1 - y) * stride, stride);
        }

        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, flipped, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private void UpdateThumbnailProgress(int done, int total)
    {
        ThumbnailProgress.Maximum = Math.Max(1, total);
        ThumbnailProgress.Value = done;
        ThumbnailProgress.Visibility = total > 0 && done < total ? Visibility.Visible : Visibility.Collapsed;
        ThumbnailStatusText.Text = total > 0 && done < total
            ? $"正在生成缩略图 {done}/{total}"
            : string.Empty;
    }

    private void ResetThumbnailProgress()
    {
        ThumbnailProgress.Maximum = 1;
        ThumbnailProgress.Value = 0;
        ThumbnailProgress.Visibility = Visibility.Collapsed;
        ThumbnailStatusText.Text = string.Empty;
    }

    private void ResetCurrentEntry()
    {
        currentEntryStream?.Dispose();
        currentEntryStream = null;
        currentObjectRow = null;
        currentTexture = null;
        currentObjectKey = null;
        showingFullRes = false;
    }

    private void ClearObjects()
    {
        ResetCurrentEntry();
        objects.Clear();
        DetailTextBox.Text = string.Empty;
        SetPreview(null);
        StatusText.Text = string.Empty;
        UpdateFooter();
    }

    private void UpdateFooter()
    {
        int bundleCount = bundles.Count;
        int objectCount = objects.Count;
        FooterText.Text = $"Bundles={bundleCount}  Objects={objectCount}";
    }

    protected override void OnClosed(EventArgs e)
    {
        ClearAllBundles();
        thumbnails.Dispose();
        base.OnClosed(e);
    }
}

internal sealed class LoadedBundle
{
    public required string FilePath { get; init; }
    public required string RelativePath { get; init; }
    public required UnityFsReader Reader { get; init; }
}

internal sealed record BundleNode(LoadedBundle Bundle);

internal sealed record EntryNode(LoadedBundle Bundle, int EntryIndex);

internal enum ChannelView
{
    Rgba,
    Rgb,
    Alpha,
}

public sealed class ObjectRow
{
    public ObjectRow(
        long pathId,
        int typeId,
        long byteStart,
        int byteSize,
        int serializedTypeIndex,
        short scriptTypeIndex,
        bool isStripped,
        SerializedObjectInfo info)
    {
        PathId = pathId;
        TypeId = typeId;
        ByteStart = byteStart;
        ByteSize = byteSize;
        SerializedTypeIndex = serializedTypeIndex;
        ScriptTypeIndex = scriptTypeIndex;
        IsStripped = isStripped;
        Info = info;
    }

    public long PathId { get; }
    public int TypeId { get; }
    public long ByteStart { get; }
    public int ByteSize { get; }
    public int SerializedTypeIndex { get; }
    public short ScriptTypeIndex { get; }
    public bool IsStripped { get; }
    [System.ComponentModel.Browsable(false)]
    public SerializedObjectInfo Info { get; }
}
