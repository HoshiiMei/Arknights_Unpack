using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ArkBrowser.Core.Decryption;
using ArkBrowser.Core.Serialization;
using Microsoft.Win32;

namespace ArkBrowser.UI;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<EntryRow> entries = [];
    private readonly ObservableCollection<ObjectRow> objects = [];

    private UnityFsReader? reader;
    private Stream? currentEntryStream;

    public MainWindow()
    {
        InitializeComponent();

        EntriesGrid.ItemsSource = entries;
        ObjectsGrid.ItemsSource = objects;

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
        reader?.Dispose();
        reader = UnityFsReader.Open(path);

        PathTextBox.Text = path;
        StatusText.Text = string.Empty;

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
                objectInfo.IsStripped));
        }

        StatusText.Text =
            $"{row.Path}  Types={serializedFile.Types.Count}  Objects={serializedFile.Objects.Count}  DataOffset={serializedFile.Header.DataOffset}";
        UpdateFooter();
    }

    private void ResetEntrySelection()
    {
        EntriesGrid.SelectedItem = null;
        ResetCurrentEntry();
        objects.Clear();
    }

    private void ResetCurrentEntry()
    {
        currentEntryStream?.Dispose();
        currentEntryStream = null;
    }

    private void ClearObjects()
    {
        ResetCurrentEntry();
        objects.Clear();
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
        bool isStripped)
    {
        PathId = pathId;
        TypeId = typeId;
        ByteStart = byteStart;
        ByteSize = byteSize;
        SerializedTypeIndex = serializedTypeIndex;
        ScriptTypeIndex = scriptTypeIndex;
        IsStripped = isStripped;
    }

    public long PathId { get; }
    public int TypeId { get; }
    public long ByteStart { get; }
    public int ByteSize { get; }
    public int SerializedTypeIndex { get; }
    public short ScriptTypeIndex { get; }
    public bool IsStripped { get; }
}
