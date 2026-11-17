using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Mister.App.ViewModels;

namespace Mister.App.Views;

public partial class ArchiveInspectorView : UserControl
{
    private string typeAhead = string.Empty;
    private DateTime typeAheadAt = DateTime.MinValue;

    public ArchiveInspectorView()
    {
        InitializeComponent();
        PreviewTextInput += OnTypeAhead;
    }

    public event EventHandler<SongExtractRequest>? ExtractRequested;

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (DataContext is ArchiveInspectorViewModel inspector) inspector.CloseFolder();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (DataContext is ArchiveInspectorViewModel inspector) inspector.OpenSelectedFolder();
    }

    private void OnTypeAhead(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)
            || e.Text.Any(static character => char.IsControl(character))
            || IsTextEntryFocused()
            || DataContext is not ArchiveInspectorViewModel inspector)
        {
            return;
        }

        e.Handled = true;
        DateTime now = DateTime.UtcNow;
        bool fresh = (now - typeAheadAt).TotalMilliseconds > 800;
        typeAheadAt = now;
        string previous = fresh ? string.Empty : typeAhead;
        bool cycle = e.Text.Length == 1
            && previous.Length > 0
            && previous.All(character =>
                char.ToUpperInvariant(character) == char.ToUpperInvariant(e.Text[0]));
        string prefix = cycle ? e.Text : previous + e.Text;
        int start = cycle ? FileList.SelectedIndex + 1 : 0;
        int found = IndexStartingWith(inspector.VisibleRows, prefix, start);
        if (found < 0)
        {
            typeAhead = previous;
            return;
        }

        typeAhead = prefix;
        ArchiveBrowserRowViewModel row = inspector.VisibleRows[found];
        FileList.SelectedItem = row;
        FileList.ScrollIntoView(row);
    }

    private static int IndexStartingWith(
        IList<ArchiveBrowserRowViewModel> rows,
        string prefix,
        int start)
    {
        if (rows.Count == 0) return -1;
        if (start < 0) start = 0;
        for (int offset = 0; offset < rows.Count; offset++)
        {
            int index = (start + offset) % rows.Count;
            if (rows[index].Title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsTextEntryFocused()
    {
        DependencyObject? current = Keyboard.FocusedElement as DependencyObject;
        while (current is not null)
        {
            if (current is TextBoxBase or ComboBox) return true;
            current = VisualTreeHelper.GetParent(current)
                ?? LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private void OnFileListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(FileList, source) is not ListBoxItem item) return;
        if (item.Content is not SongFolderRowViewModel folder) return;
        if (DataContext is not ArchiveInspectorViewModel inspector) return;
        inspector.OpenFolder(folder);
    }

    private void OnExtractFolders(object sender, RoutedEventArgs e) => RequestExtract(folders: true);

    private void OnExtractFiles(object sender, RoutedEventArgs e) => RequestExtract(folders: false);

    private void RequestExtract(bool folders)
    {
        var paths = new List<string>();
        var names = new List<string>();
        foreach (object item in FileList.SelectedItems)
        {
            if (folders && item is SongFolderRowViewModel folder)
            {
                names.Add(folder.SongName);
                paths.AddRange(folder.Files.Select(static file => file.DecodedPath));
            }
            else if (!folders && item is ArchiveFileRowViewModel file)
            {
                names.Add(file.Title);
                paths.Add(file.Node.DecodedPath);
            }
        }

        if (paths.Count == 0) return;
        string[] distinct = paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string description = folders
            ? names.Count == 1
                ? $"Extract the song folder {names[0]} ({distinct.Length:N0} files), including its audio and chart files."
                : $"Extract {names.Count:N0} song folders ({distinct.Length:N0} files)."
            : names.Count == 1
                ? $"Extract {names[0]}."
                : $"Extract {distinct.Length:N0} audio or chart files from this song.";
        ExtractRequested?.Invoke(this, new SongExtractRequest(distinct, description));
    }
}

public sealed class SongExtractRequest : EventArgs
{
    public SongExtractRequest(IReadOnlyList<string> paths, string description)
    {
        Paths = paths;
        Description = description;
    }

    public IReadOnlyList<string> Paths { get; }
    public string Description { get; }
}
