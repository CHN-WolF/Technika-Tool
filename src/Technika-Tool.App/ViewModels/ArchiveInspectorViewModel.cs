using System.Collections.ObjectModel;
using TechnikaTool.Core.Formats;
using TechnikaTool.Core.Preview;

namespace TechnikaTool.App.ViewModels;

public sealed class ArchiveInspectorViewModel : ViewModelBase
{
    private readonly IReadOnlyList<ArchiveTreeNodeViewModel> allEntries;
    private readonly IReadOnlyList<SongFolderRowViewModel> songFolders;
    private readonly Dictionary<ArchiveTreeNodeViewModel, ArchiveFileRowViewModel> fileRows = new();
    private string searchText = string.Empty;
    private string extensionFilter = string.Empty;
    private ArchiveTreeNodeViewModel? selectedEntry;
    private ArchiveBrowserRowViewModel? selectedRow;
    private SongFolderRowViewModel? currentFolder;

    public ArchiveInspectorViewModel(IEnumerable<ArchiveEntry> entries)
        : this(entries.Select(static entry => new ArchiveTreeNodeViewModel(entry)))
    {
    }

    public ArchiveInspectorViewModel(IEnumerable<ArchiveEntrySource> entries)
        : this(entries.Select(static entry => new ArchiveTreeNodeViewModel(entry)))
    {
    }

    private ArchiveInspectorViewModel(IEnumerable<ArchiveTreeNodeViewModel> entries)
    {
        allEntries = entries.ToArray();
        songFolders = SongFolderIndex.Group(allEntries);
        IsSongBrowser = songFolders.Count > 0;
        VisibleRows = new ObservableCollection<ArchiveBrowserRowViewModel>();
        Details = new EntryDetailsViewModel();
        ApplyFilter();
    }

    public ObservableCollection<ArchiveBrowserRowViewModel> VisibleRows { get; }
    public EntryDetailsViewModel Details { get; }
    public bool IsSongBrowser { get; }
    public bool IsSongList => IsSongBrowser && currentFolder is null;
    public bool IsInsideSong => currentFolder is not null;
    public string CurrentFolderTitle => currentFolder?.SongName ?? string.Empty;
    public string SearchPlaceholder => IsSongList ? "Search songs" : "Search files";
    public string EmptyMessage => IsInsideSong
        ? "No audio or chart files match this filter."
        : IsSongBrowser
            ? "No song folders match this search."
            : "Files appear here after the selected pack validates.";
    public string BrowserHint => IsInsideSong
        ? "Select audio or chart files, then extract the selection."
        : IsSongBrowser
            ? "Double-click a song to open it. Extract folder writes every file in that song."
            : string.Empty;
    public bool CanOpenFolder => IsSongList && selectedRow is SongFolderRowViewModel;
    public bool CanExtractFolder => CanOpenFolder;
    public bool CanExtractFiles => IsInsideSong && selectedRow is ArchiveFileRowViewModel;

    public SongFolderRowViewModel? SelectedFolder =>
        selectedRow as SongFolderRowViewModel;

    public string SearchText
    {
        get => searchText;
        set
        {
            if (SetProperty(ref searchText, value ?? string.Empty)) ApplyFilter();
        }
    }

    public string ExtensionFilter
    {
        get => extensionFilter;
        set
        {
            if (SetProperty(ref extensionFilter, value ?? string.Empty)) ApplyFilter();
        }
    }

    public ArchiveBrowserRowViewModel? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (ReferenceEquals(selectedRow, value)) return;
            selectedRow = value;
            SelectedEntry = value is ArchiveFileRowViewModel file ? file.Node : null;
            OnPropertyChanged(nameof(SelectedRow));
            OnPropertyChanged(nameof(SelectedFolder));
            OnPropertyChanged(nameof(CanOpenFolder));
            OnPropertyChanged(nameof(CanExtractFolder));
            OnPropertyChanged(nameof(CanExtractFiles));
        }
    }

    public ArchiveTreeNodeViewModel? SelectedEntry
    {
        get => selectedEntry;
        private set
        {
            if (SetProperty(ref selectedEntry, value)) Details.Show(value);
        }
    }

    public void OpenSelectedFolder()
    {
        if (SelectedRow is SongFolderRowViewModel folder) OpenFolder(folder);
    }

    public void OpenFolder(SongFolderRowViewModel folder)
    {
        currentFolder = folder;
        searchText = string.Empty;
        extensionFilter = string.Empty;
        NotifyBrowserChrome();
        ApplyFilter();
    }

    public void CloseFolder()
    {
        if (currentFolder is null) return;
        currentFolder = null;
        searchText = string.Empty;
        extensionFilter = string.Empty;
        NotifyBrowserChrome();
        ApplyFilter();
    }

    private void NotifyBrowserChrome()
    {
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(ExtensionFilter));
        OnPropertyChanged(nameof(IsSongList));
        OnPropertyChanged(nameof(IsInsideSong));
        OnPropertyChanged(nameof(CurrentFolderTitle));
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(BrowserHint));
        OnPropertyChanged(nameof(CanOpenFolder));
        OnPropertyChanged(nameof(CanExtractFolder));
        OnPropertyChanged(nameof(CanExtractFiles));
    }

    private void ApplyFilter()
    {
        ArchiveBrowserRowViewModel? previous = selectedRow;
        VisibleRows.Clear();
        if (currentFolder is not null)
        {
            foreach (ArchiveTreeNodeViewModel file in currentFolder.Files)
            {
                if (MatchesFile(file)) VisibleRows.Add(RowFor(file, inSongFolder: true));
            }
        }
        else if (IsSongBrowser)
        {
            foreach (SongFolderRowViewModel folder in songFolders)
            {
                if (FolderMatches(folder)) VisibleRows.Add(folder);
            }
        }
        else
        {
            foreach (ArchiveTreeNodeViewModel entry in allEntries)
            {
                if (MatchesFile(entry)) VisibleRows.Add(RowFor(entry, inSongFolder: false));
            }
        }

        if (previous is not null && VisibleRows.Contains(previous))
        {
            if (!ReferenceEquals(selectedRow, previous)) SelectedRow = previous;
        }
        else if (selectedRow is not null)
        {
            SelectedRow = null;
        }

        OnPropertyChanged(nameof(EmptyMessage));
    }

    private bool FolderMatches(SongFolderRowViewModel folder)
    {
        if (string.IsNullOrWhiteSpace(searchText)
            && string.IsNullOrWhiteSpace(extensionFilter))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(extensionFilter)
            && (folder.SongName.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                || folder.FolderPath.Contains(searchText, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (ArchiveTreeNodeViewModel file in folder.Files)
        {
            if (MatchesFile(file)) return true;
        }

        return false;
    }

    private bool MatchesFile(ArchiveTreeNodeViewModel item)
    {
        if (!string.IsNullOrWhiteSpace(searchText)
            && !item.DecodedPath.Contains(searchText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(extensionFilter)
            || string.Equals(item.Extension, extensionFilter.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private ArchiveFileRowViewModel RowFor(ArchiveTreeNodeViewModel node, bool inSongFolder)
    {
        if (fileRows.TryGetValue(node, out ArchiveFileRowViewModel? row)) return row;
        row = new ArchiveFileRowViewModel(node, inSongFolder);
        fileRows.Add(node, row);
        return row;
    }

    public void ApplyPreview(EntryPreview preview)
    {
        if (preview.Metadata.TryGetValue("signature", out string? signature))
        {
            Details.ShowSignature(signature);
        }
    }
}
