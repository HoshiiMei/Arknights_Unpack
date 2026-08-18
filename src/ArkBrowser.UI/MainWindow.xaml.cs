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
    private readonly ObservableCollection<EntryRow> entries = [];
    private readonly ObservableCollection<ObjectRow> objects = [];

    private readonly ThumbnailService thumbnails = new();
    private readonly FullResCache fullResCache = new(capacity: 3);
    private int previewToken;
    private UnityFsReader? reader;
    private Stream? currentEntryStream;
    private ObjectRow? currentObjectRow;
    private Texture2DInfo? currentTexture;
    private string? currentObjectKey;
    private bool showingFullRes;

    public MainWindow()
    {
        InitializeComponent();

        EntriesGrid.ItemsSource = entries;
        ObjectsGrid.ItemsSource = objects;
        thumbnails.ProgressChanged += (done, total) => Dispatcher.BeginInvoke(() => UpdateThumbnailProgress(done, total));
        thumbnails.ThumbnailReady += (key, thumbnail) => Dispatcher.BeginInvoke(() => OnThumbnailReady(key, thumbnail));

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

    private void OpenBundle(string path)
    {
        ResetEntrySelection();
        thumbnails.Clear();
        fullResCache.Clear();
        reader?.Dispose();
        reader = UnityFsReader.Open(path);

        PathTextBox.Text = path;
        StatusText.Text = string.Empty;
        DetailTextBox.Text = string.Empty;
        PreviewImage.Source = null;
        ResetThumbnailProgress();

        entries.Clear();
        for (int i = 0; i < reader.Entries.Count; i++)
        {
            UnityFsEntry entry = reader.Entries[i];
            entries.Add(new EntryRow(
                i,
                entry.Path,
                entry.Offset,
                entry.Size,
                entry.Flags,
                entry.IsSerializedFile));
        }

        StatusText.Text = $"已打开 {Path.GetFileName(path)}，Entries={reader.Entries.Count}";
        UpdateFooter();
    }

    private void EntriesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EntriesGrid.SelectedItem is not EntryRow row)
        {
            ClearObjects();
            return;
        }

        try
        {
            LoadEntry(row);
        }
        catch (Exception exception)
        {
            ClearObjects();
            StatusText.Text = $"解析失败：{exception.Message}";
            UpdateFooter();
        }
    }

    private void LoadEntry(EntryRow row)
    {
        ResetCurrentEntry();
        objects.Clear();
        DetailTextBox.Text = string.Empty;
        PreviewImage.Source = null;

        if (!row.IsSerializedFile)
        {
            StatusText.Text = $"{row.Path} 是资源流，跳过 SerializedFile 对象表解析。";
            UpdateFooter();
            return;
        }

        if (reader is null)
        {
            return;
        }

        currentEntryStream = reader.OpenEntry(row.Index);
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

        int entryIndex = row.Index;
        UnityFsReader capturedReader = reader;
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
            $"{row.Path}  Types={serializedFile.Types.Count}  Objects={serializedFile.Objects.Count}  DataOffset={serializedFile.Header.DataOffset}";
        UpdateFooter();
    }

    private void ObjectsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DetailTextBox.Text = string.Empty;
        PreviewImage.Source = null;
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

    private void ShowThumbnail(ObjectRow row)
    {
        string key = MakeThumbnailKey(row);
        if (thumbnails.TryGet(key, out BitmapSource? thumbnail))
        {
            PreviewImage.Source = thumbnail;
            return;
        }

        PreviewImage.Source = null;
    }

    private void OnThumbnailReady(string key, BitmapSource thumbnail)
    {
        if (showingFullRes || currentObjectKey != key)
        {
            return;
        }

        PreviewImage.Source = thumbnail;
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
            PreviewImage.Source = cached;
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
            PreviewImage.Source = bitmap;
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

    private static string MakeThumbnailKey(ObjectRow row)
    {
        return $"{row.Info.PathId}:{row.Info.ByteStart}";
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

    private void ResetEntrySelection()
    {
        EntriesGrid.SelectedItem = null;
        ResetCurrentEntry();
        objects.Clear();
        DetailTextBox.Text = string.Empty;
        PreviewImage.Source = null;
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
        PreviewImage.Source = null;
        StatusText.Text = string.Empty;
        UpdateFooter();
    }

    private void UpdateFooter()
    {
        int entryCount = EntriesGrid.Items.Count;
        int objectCount = objects.Count;
        FooterText.Text = $"Entries={entryCount}  Objects={objectCount}";
    }

    protected override void OnClosed(EventArgs e)
    {
        ResetCurrentEntry();
        thumbnails.Dispose();
        reader?.Dispose();
        reader = null;
        base.OnClosed(e);
    }
}

public sealed class EntryRow
{
    public EntryRow(int index, string path, long offset, long size, uint flags, bool isSerializedFile)
    {
        Index = index;
        Path = path;
        Offset = offset;
        Size = size;
        Flags = flags;
        IsSerializedFile = isSerializedFile;
    }

    public int Index { get; }
    public string Path { get; }
    public long Offset { get; }
    public long Size { get; }
    public uint Flags { get; }
    public bool IsSerializedFile { get; }
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
