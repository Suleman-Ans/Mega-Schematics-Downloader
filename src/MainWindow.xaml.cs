using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MegaSchematicsDownloader;

public partial class MainWindow : Window
{
    private const string SharedFolderUrl = "https://mega.nz/folder/EWFAzKIT#uUGjAxvc8TlpnQVLUHl5wg";
    private const string UploadRequestUrl = "https://mega.nz/filerequest/QK_jfHIDbGk";
    private const string GithubUrl = "https://github.com/KiKiHUN1/Mega-Schematics-Downloader";
    private const string AppTitle = "Mega Schematics Downloader";

    private readonly MegaService _mega = new();
    private readonly DownloadManager _downloader;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _syncTimer;
    private readonly AppSettings _settings;

    private string _downloadRoot;
    private DatabaseIndex? _database;
    private MegaItem? _currentFolder;
    private List<NodeEntry> _entries = new();
    private SortKind _sortKind;
    private CancellationTokenSource? _cts;
    private bool _showingSearch;
    private bool _isBusy;
    private bool _bulkRunning;
    private bool _syncing;
    private bool _live;
    private Stopwatch? _syncWatch;
    private bool _initializing = true;

    public MainWindow()
    {
        InitializeComponent();

        _downloader = new DownloadManager(_mega);
        _settings = AppSettings.Load();
        _downloadRoot = ResolveDownloadRoot(_settings.DownloadRoot);
        _sortKind = Enum.IsDefined(typeof(SortKind), _settings.Sort) ? (SortKind)_settings.Sort : SortKind.NameAsc;
        CurrentFolderOnlyCheck.IsChecked = _settings.SearchCurrentFolderOnly;

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += SearchTimer_Tick;
        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _syncTimer.Tick += SyncTimer_Tick;

        UpdateSortLabel();
        UpdateFolderText();
        LocationText.Text = "Connecting to the community database...";
        StatusText.Text = "Starting...";
        UpdateControls();
        _initializing = false;
    }

    // ---------------------------------------------------------------- loading

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadDatabaseAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadDatabaseAsync();

    private async Task LoadDatabaseAsync()
    {
        if (_syncing || _isBusy) return;
        _syncing = true;

        DateTime? savedUtc = null;
        if (_database is null) savedUtc = await TryShowSavedIndexAsync();

        if (savedUtc.HasValue)
        {
            StatusText.Text = $"Showing the saved index from {savedUtc.Value.ToLocalTime():d MMM yyyy}. " +
                              "Browsing works now; downloads unlock when the sync finishes.";
        }
        else
        {
            StatusText.Text = "Loading the file index from MEGA. The first load can take a while; " +
                              "later starts open instantly.";
        }
        StartSyncTimer();
        UpdateControls();

        try
        {
            var nodes = await _mega.GetNodesAsync(new Uri(SharedFolderUrl));

            if (_database is not null && _database.TryAttach(nodes))
            {
                // The saved index matches MEGA exactly, so nothing on screen needs to change.
            }
            else
            {
                var index = await Task.Run(() => new DatabaseIndex(nodes));
                SwapIndex(index);
                _ = Task.Run(() => IndexCache.Save(index.ToCache(), SharedFolderUrl));
            }

            _live = true;
            var current = _database!;
            StatusText.Text =
                $"Database loaded: {current.FileCount:N0} files in {current.DirectoryCount:N0} folders " +
                $"({PathHelper.FormatSize(current.TotalBytes)}).";
        }
        catch (Exception ex)
        {
            if (_database is null)
            {
                StatusText.Text = "Could not load the database. Check your connection and press Refresh.";
                LocationText.Text = "Not connected";
                ShowError("The MEGA database could not be loaded.", ex);
            }
            else
            {
                StatusText.Text = "Could not reach MEGA, so you are viewing the saved index. " +
                                  "Downloads are off until it connects. Press Refresh to retry.";
            }
        }
        finally
        {
            StopSyncTimer();
            _syncing = false;
            UpdateControls();
        }
    }

    private async Task<DateTime?> TryShowSavedIndexAsync()
    {
        try
        {
            var cached = await Task.Run(() => IndexCache.Load(SharedFolderUrl));
            if (cached is null) return null;

            var index = await Task.Run(() => new DatabaseIndex(cached.Items));
            _database = index;
            _currentFolder = index.Root;
            _showingSearch = false;
            _live = false;
            SearchBox.Clear();
            RenderCurrentFolder();
            return cached.SavedUtc;
        }
        catch
        {
            _database = null;
            _currentFolder = null;
            return null;
        }
    }

    /// <summary>Replaces the index but keeps the user on the same folder or search.</summary>
    private void SwapIndex(DatabaseIndex index)
    {
        var folderId = _currentFolder?.Id;
        var query = SearchBox.Text;
        var wasSearching = _showingSearch && !string.IsNullOrWhiteSpace(query);

        _database = index;
        _currentFolder = (folderId is not null ? index.GetById(folderId) : null) ?? index.Root;

        if (wasSearching) RenderSearchResults(query);
        else RenderCurrentFolder();
    }

    private void StartSyncTimer()
    {
        _syncWatch = Stopwatch.StartNew();
        SyncText.Visibility = Visibility.Visible;
        UpdateSyncText();
        _syncTimer.Start();
    }

    private void StopSyncTimer()
    {
        _syncTimer.Stop();
        _syncWatch = null;
        SyncText.Text = string.Empty;
        SyncText.Visibility = Visibility.Collapsed;
    }

    private void SyncTimer_Tick(object? sender, EventArgs e) => UpdateSyncText();

    private void UpdateSyncText()
    {
        if (_syncWatch is null) return;
        SyncText.Text = $"Syncing with MEGA... {(int)_syncWatch.Elapsed.TotalSeconds} s";
    }

    // -------------------------------------------------------------- rendering

    private bool IsAtRoot() =>
        _database is not null && _currentFolder is not null && _database.IsRoot(_currentFolder);

    private void ShowItems(IEnumerable<MegaItem> source, bool showLocation)
    {
        var sorted = SortItems(source);
        // Swap the whole list at once: one binding refresh instead of one per row.
        _entries = sorted.Select(x => new NodeEntry(x, _downloadRoot, showLocation)).ToList();
        ItemsList.ItemsSource = _entries;
    }

    private List<MegaItem> SortItems(IEnumerable<MegaItem> source)
    {
        var ordered = source.OrderBy(x => x.IsFile ? 1 : 0);
        switch (_sortKind)
        {
            case SortKind.NameDesc:
                return ordered.ThenByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            case SortKind.SizeDesc:
                return ordered
                    .ThenByDescending(x => x.IsFile ? x.Size : x.TotalBytes)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            default:
                return ordered.ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private void RenderCurrentFolder()
    {
        if (_database is null || _currentFolder is null) return;
        _showingSearch = false;
        ShowItems(_database.GetChildren(_currentFolder), false);

        LocationText.Text = IsAtRoot() ? "Database /" : "Database / " + _currentFolder.DisplayPath;
        CountText.Text = $"{_entries.Count:N0} items";
        UpdateControls();
    }

    private void RenderSearchResults(string query)
    {
        if (_database is null) return;
        var scope = CurrentFolderOnlyCheck.IsChecked == true ? _currentFolder : null;
        var results = _database.Search(query, scope);

        _showingSearch = true;
        ShowItems(results, true);

        var text = query.Trim();
        var scoped = scope is not null && !_database.IsRoot(scope);
        LocationText.Text = scoped ? $"Search in {scope!.DisplayPath}: {text}" : $"Search results for: {text}";
        CountText.Text = $"{_entries.Count:N0} files";
        StatusText.Text = _entries.Count == 0 ? "No matching files." : $"Found {_entries.Count:N0} matching files.";
        UpdateControls();
    }

    private void ReRender()
    {
        if (_database is null) return;
        if (_showingSearch && !string.IsNullOrWhiteSpace(SearchBox.Text)) RenderSearchResults(SearchBox.Text);
        else RenderCurrentFolder();
    }

    private void RefreshEntries()
    {
        foreach (var entry in _entries) entry.Refresh();
    }

    private void UpdateControls()
    {
        var hasDatabase = _database is not null;
        var idle = !_isBusy;

        var canDownload = idle && hasDatabase && _live && !_syncing;

        RefreshButton.IsEnabled = idle && !_syncing;
        BackButton.IsEnabled = idle && hasDatabase && !_showingSearch && _currentFolder is not null &&
                               _database!.GetParent(_currentFolder) is not null;
        HomeButton.IsEnabled = idle && hasDatabase && (_showingSearch || !IsAtRoot());
        SortButton.IsEnabled = idle;
        ChangeFolderButton.IsEnabled = idle;

        DownloadSelectedButton.IsEnabled = canDownload && ItemsList.SelectedItems.Count > 0;
        DownloadViewButton.IsEnabled = canDownload && _entries.Count > 0;
        DownloadViewButton.Content = _showingSearch ? "Download all results" : "Download this folder";
        DownloadAllButton.IsEnabled = canDownload;

        ProgressPanel.Visibility = _bulkRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _isBusy = busy;
        if (message is not null) StatusText.Text = message;
        Mouse.OverrideCursor = busy && !_bulkRunning ? Cursors.Wait : null;
        UpdateControls();
    }

    private void UpdateSortLabel()
    {
        SortButton.Content = _sortKind switch
        {
            SortKind.NameDesc => "Sort: Name Z-A",
            SortKind.SizeDesc => "Sort: Size",
            _ => "Sort: Name A-Z"
        };
    }

    private void UpdateFolderText()
    {
        FolderText.Text = "Saving to: " + _downloadRoot;
        FolderText.ToolTip = _downloadRoot;
    }

    // ------------------------------------------------------------- navigation

    private void ClearSearchBox()
    {
        // Mark search as over first so clearing the box does not trigger a second render.
        _showingSearch = false;
        _searchTimer.Stop();
        SearchBox.Clear();
    }

    private void GoBack()
    {
        if (_isBusy || _database is null || _currentFolder is null || _showingSearch) return;
        var parent = _database.GetParent(_currentFolder);
        if (parent is null) return;
        _currentFolder = parent;
        ClearSearchBox();
        RenderCurrentFolder();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        if (_database is null) return;
        _currentFolder = _database.Root;
        ClearSearchBox();
        RenderCurrentFolder();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            if (_showingSearch && _database is not null) RenderCurrentFolder();
            return;
        }
        if (_database is not null) _searchTimer.Start();
    }

    private void SearchTimer_Tick(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        if (_database is not null && !string.IsNullOrWhiteSpace(SearchBox.Text))
            RenderSearchResults(SearchBox.Text);
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) => SearchBox.Clear();

    private void ScopeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.SearchCurrentFolderOnly = CurrentFolderOnlyCheck.IsChecked == true;
        _settings.Save();
        if (_showingSearch && !string.IsNullOrWhiteSpace(SearchBox.Text)) RenderSearchResults(SearchBox.Text);
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        _sortKind = _sortKind switch
        {
            SortKind.NameAsc => SortKind.NameDesc,
            SortKind.NameDesc => SortKind.SizeDesc,
            _ => SortKind.NameAsc
        };
        _settings.Sort = (int)_sortKind;
        _settings.Save();
        UpdateSortLabel();
        ReRender();
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();

    // ----------------------------------------------------------- row actions

    private async void ItemAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NodeEntry entry })
            await GuardAsync(() => ActivateAsync(entry));
    }

    private async void Item_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // A double click on the row's own button is already handled by that button.
        if (IsWithinButton(e.OriginalSource as DependencyObject)) return;
        if (sender is ListViewItem { DataContext: NodeEntry entry })
        {
            e.Handled = true;
            await GuardAsync(() => ActivateAsync(entry));
        }
    }

    private static bool IsWithinButton(DependencyObject? node)
    {
        while (node is not null && node is not ListViewItem)
        {
            if (node is Button) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private async Task ActivateAsync(NodeEntry entry)
    {
        if (_isBusy || _database is null) return;

        if (entry.IsDirectory)
        {
            _currentFolder = entry.Item;
            ClearSearchBox();
            RenderCurrentFolder();
            return;
        }

        if (entry.IsOnDisk)
        {
            ShowInExplorer(entry.DestinationPath);
            return;
        }

        if (!_live || _syncing)
        {
            StatusText.Text = _syncing
                ? "Still syncing with MEGA. Downloads unlock in a moment."
                : "Not connected to MEGA. Press Refresh, then try again.";
            return;
        }

        await DownloadAsync(new[] { entry.Item }, false, $"\"{entry.Name}\"");
    }

    // --------------------------------------------------------- bulk download

    private async void DownloadSelected_Click(object sender, RoutedEventArgs e) =>
        await GuardAsync(DownloadSelectedAsync);

    private async void DownloadView_Click(object sender, RoutedEventArgs e) =>
        await GuardAsync(DownloadViewAsync);

    private async void DownloadAll_Click(object sender, RoutedEventArgs e) =>
        await GuardAsync(DownloadAllAsync);

    private async Task DownloadSelectedAsync()
    {
        if (_database is null) return;
        var picked = ItemsList.SelectedItems.OfType<NodeEntry>().Select(x => x.Item).ToList();
        if (picked.Count == 0) return;

        var files = CollectFiles(picked);
        var label = picked.Count == 1 ? $"\"{picked[0].Name}\"" : $"{picked.Count:N0} selected items";
        await DownloadAsync(files, files.Count > 1, label);
    }

    private async Task DownloadViewAsync()
    {
        if (_database is null || _currentFolder is null) return;

        List<MegaItem> files;
        string label;
        if (_showingSearch)
        {
            files = _entries.Where(x => !x.IsDirectory).Select(x => x.Item).ToList();
            label = $"{files.Count:N0} search results";
        }
        else
        {
            files = _database.GetFilesUnder(_currentFolder);
            label = IsAtRoot() ? "the full database" : $"the folder \"{_currentFolder.Name}\"";
        }
        await DownloadAsync(files, files.Count > 1, label);
    }

    private async Task DownloadAllAsync()
    {
        if (_database is null) return;
        var files = _database.GetFilesUnder(_database.Root);
        await DownloadAsync(files, true, $"the full database ({files.Count:N0} files)");
    }

    private List<MegaItem> CollectFiles(IEnumerable<MegaItem> items)
    {
        var result = new List<MegaItem>();
        if (_database is null) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            foreach (var file in _database.GetFilesUnder(item))
            {
                if (seen.Add(file.Id)) result.Add(file);
            }
        }
        return result;
    }

    private async Task DownloadAsync(IReadOnlyList<MegaItem> files, bool confirm, string label)
    {
        if (_isBusy || _database is null || _syncing || !_live) return;
        if (files.Count == 0)
        {
            StatusText.Text = "There are no files to download here.";
            return;
        }

        var pending = files.Where(x => !File.Exists(Path.Combine(_downloadRoot, x.RelativePath))).ToList();
        if (pending.Count == 0)
        {
            StatusText.Text = files.Count == 1
                ? "Already downloaded."
                : $"Nothing to download: all {files.Count:N0} files are already on disk.";
            return;
        }

        long pendingBytes = 0;
        foreach (var item in pending) pendingBytes += item.Size;

        if (confirm)
        {
            var message = new StringBuilder();
            message.AppendLine($"Download {label}?");
            message.AppendLine();
            message.AppendLine($"{pending.Count:N0} files to download ({PathHelper.FormatSize(pendingBytes)}).");
            if (pending.Count < files.Count)
                message.AppendLine($"{files.Count - pending.Count:N0} files are already on disk and will be skipped.");
            message.AppendLine($"Saving to: {_downloadRoot}");

            var free = GetFreeSpace(_downloadRoot);
            if (free.HasValue && free.Value < pendingBytes)
            {
                message.AppendLine();
                message.AppendLine($"Warning: only {PathHelper.FormatSize(free.Value)} is free on that drive.");
            }

            message.AppendLine();
            message.AppendLine("You can cancel at any time. If MEGA's transfer limit is reached, run the same " +
                               "download again later and it will continue where it stopped.");

            var answer = MessageBox.Show(this, message.ToString(), AppTitle, MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }

        _cts = new CancellationTokenSource();
        _bulkRunning = true;
        CancelButton.IsEnabled = true;
        DownloadProgressBar.IsIndeterminate = false;
        DownloadProgressBar.Value = 0;
        ProgressText.Text = "Starting...";
        SetBusy(true, $"Downloading {label}...");

        var progress = new Progress<DownloadProgress>(OnDownloadProgress);
        DownloadResult? result = null;
        try
        {
            result = await _downloader.RunAsync(pending, _downloadRoot, progress, _cts.Token);
        }
        catch (Exception ex)
        {
            StatusText.Text = "The download stopped unexpectedly.";
            ShowError("The download stopped unexpectedly.", ex);
        }
        finally
        {
            _bulkRunning = false;
            _cts?.Dispose();
            _cts = null;
            DownloadProgressBar.IsIndeterminate = false;
            SetBusy(false);
            RefreshEntries();
        }

        if (result is not null) ReportResult(result);
    }

    private void OnDownloadProgress(DownloadProgress p)
    {
        if (!_bulkRunning) return;

        var fraction = p.BytesTotal > 0
            ? (double)p.BytesDone / p.BytesTotal
            : (p.Total > 0 ? (double)p.Done / p.Total : 0d);
        DownloadProgressBar.Value = Math.Clamp(fraction, 0d, 1d) * 100d;
        // With a single file there is no per-file progress, so show a moving bar instead of an empty one.
        DownloadProgressBar.IsIndeterminate = p.Total == 1 && p.Done < p.Total;

        ProgressText.Text =
            $"{p.Done:N0} / {p.Total:N0} files  |  {PathHelper.FormatSize(p.BytesDone)} / " +
            $"{PathHelper.FormatSize(p.BytesTotal)}  |  {p.CurrentFile}";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is null) return;
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        ProgressText.Text = "Cancelling after the current file finishes...";
    }

    private void ReportResult(DownloadResult r)
    {
        var lead = r.Cancelled ? "Cancelled."
            : r.QuotaReached ? "Stopped: MEGA transfer limit reached."
            : r.DiskFull ? "Stopped: the disk is full."
            : "Finished.";

        var parts = new List<string> { $"{r.Downloaded:N0} downloaded" };
        if (r.Skipped > 0) parts.Add($"{r.Skipped:N0} already on disk");
        if (r.Failed > 0) parts.Add($"{r.Failed:N0} failed");
        if (r.NotAttempted > 0) parts.Add($"{r.NotAttempted:N0} not attempted");
        var summary = $"{lead} {string.Join(", ", parts)} ({PathHelper.FormatSize(r.Bytes)}).";
        StatusText.Text = summary;

        var hasIssue = r.Failed > 0 || r.QuotaReached || r.DiskFull;
        if (r.Cancelled && !hasIssue) return;
        if (!hasIssue && r.Downloaded + r.Skipped <= 1) return;

        var text = new StringBuilder(summary);
        if (r.QuotaReached)
        {
            text.AppendLine().AppendLine();
            text.Append("MEGA limits how much an anonymous connection can transfer. Wait a while and run the " +
                        "same download again. Files already on disk are skipped, so it resumes automatically.");
        }
        if (r.DiskFull)
        {
            text.AppendLine().AppendLine();
            text.Append("Free some space or choose another folder with \"Change folder\", then run it again.");
        }
        if (r.Errors.Count > 0)
        {
            text.AppendLine().AppendLine();
            text.AppendLine("Failed files:");
            foreach (var error in r.Errors.Take(10)) text.AppendLine("- " + error);
            if (r.Errors.Count > 10) text.AppendLine($"... and {r.Errors.Count - 10:N0} more.");
        }

        MessageBox.Show(this, text.ToString(), AppTitle, MessageBoxButton.OK,
            hasIssue ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private static long? GetFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- folders

    private void OpenDownloads_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_downloadRoot);
            OpenFolder(_downloadRoot);
        }
        catch (Exception ex)
        {
            ShowError("The download folder could not be opened.", ex);
        }
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where downloads are saved",
            InitialDirectory = Directory.Exists(_downloadRoot) ? _downloadRoot : string.Empty
        };
        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;

        _downloadRoot = dialog.FolderName;
        _settings.DownloadRoot = _downloadRoot;
        _settings.Save();
        UpdateFolderText();
        ReRender();
        StatusText.Text = "Downloads will now be saved to the new folder.";
    }

    private static string ResolveDownloadRoot(string? saved)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents)) documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fallback = Path.Combine(documents, "Mega Schematics");

        if (string.IsNullOrWhiteSpace(saved)) return fallback;
        try
        {
            return Path.IsPathRooted(saved) ? Path.GetFullPath(saved) : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // Explorer failing to open must not turn a finished download into an error.
        }
    }

    private static void ShowInExplorer(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
        }
        catch
        {
            // Same as above.
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError("The link could not be opened.", ex);
        }
    }

    private void Upload_Click(object sender, RoutedEventArgs e) => OpenUrl(UploadRequestUrl);
    private void Github_Click(object sender, RoutedEventArgs e) => OpenUrl(GithubUrl);

    // ------------------------------------------------------- window behaviour

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            await LoadDatabaseAsync();
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            SearchBox.Clear();
            ItemsList.Focus();
        }
        else if (e.Key == Key.Enter && ItemsList.IsKeyboardFocusWithin &&
                 ItemsList.SelectedItems.Count == 1 && ItemsList.SelectedItem is NodeEntry selected)
        {
            e.Handled = true;
            await GuardAsync(() => ActivateAsync(selected));
        }
        else if (e.Key == Key.Back && !SearchBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            GoBack();
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (!_bulkRunning) return;

        var answer = MessageBox.Show(this, "A download is still running. Stop it and exit?", AppTitle,
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _cts?.Cancel();
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Something went wrong.";
            ShowError("An unexpected error occurred.", ex);
        }
    }

    private void ShowError(string message, Exception ex)
    {
        MessageBox.Show(this, $"{message}\n\n{ex.Message}", AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
