using System.ComponentModel;
using System.IO;
using System.Windows;

namespace MegaSchematicsDownloader;

/// <summary>One row in the list. On-disk status is checked lazily, only for rows that get displayed.</summary>
public sealed class NodeEntry : INotifyPropertyChanged
{
    private readonly string _downloadRoot;
    private readonly bool _showLocation;
    private bool? _isOnDisk;

    public NodeEntry(MegaItem item, string downloadRoot, bool showLocation)
    {
        Item = item;
        _downloadRoot = downloadRoot;
        _showLocation = showLocation;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MegaItem Item { get; }
    public string Name => Item.Name;
    public bool IsDirectory => !Item.IsFile;

    public string DestinationPath => Path.Combine(_downloadRoot, Item.RelativePath);

    public bool IsOnDisk
    {
        get
        {
            if (IsDirectory) return false;
            _isOnDisk ??= CheckExists();
            return _isOnDisk.Value;
        }
    }

    public string KindLabel
    {
        get
        {
            if (IsDirectory) return "FOLDER";
            var extension = Path.GetExtension(Item.Name).TrimStart('.');
            return extension.Length is > 0 and <= 5 ? extension.ToUpperInvariant() : "FILE";
        }
    }

    public string SizeText => IsDirectory
        ? (Item.FileCount == 0 ? "empty" : $"{Item.FileCount:N0} files, {PathHelper.FormatSize(Item.TotalBytes)}")
        : PathHelper.FormatSize(Item.Size);

    public string StatusText => IsOnDisk ? "On disk" : string.Empty;

    public string ActionLabel => IsDirectory ? "Open" : (IsOnDisk ? "Show" : "Download");

    public string LocationText => Item.DisplayDirectory;

    public Visibility LocationVisibility =>
        _showLocation && Item.DisplayDirectory.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Forget the cached disk check, for example after a download finished.</summary>
    public void Refresh()
    {
        _isOnDisk = null;
        OnChanged(nameof(IsOnDisk));
        OnChanged(nameof(StatusText));
        OnChanged(nameof(ActionLabel));
    }

    private bool CheckExists()
    {
        try { return File.Exists(DestinationPath); }
        catch { return false; }
    }

    private void OnChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
