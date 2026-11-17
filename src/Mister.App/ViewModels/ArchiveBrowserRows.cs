using Mister.Core.Formats;

namespace Mister.App.ViewModels;

public abstract class ArchiveBrowserRowViewModel
{
    public abstract string Title { get; }
    public abstract string Subtitle { get; }
    public abstract string Badge { get; }
    public abstract string Meta { get; }
    public abstract string DetailLine { get; }
    public abstract string ToolTipText { get; }
}

public sealed class SongFolderRowViewModel : ArchiveBrowserRowViewModel
{
    public SongFolderRowViewModel(
        string folderPath,
        string songName,
        IReadOnlyList<ArchiveTreeNodeViewModel> files)
    {
        FolderPath = folderPath;
        SongName = songName;
        Files = files;
        long total = 0;
        foreach (ArchiveTreeNodeViewModel file in files)
        {
            if (file.Entry.DecodedSize > 0)
            {
                total = total > long.MaxValue - file.Entry.DecodedSize
                    ? long.MaxValue
                    : total + file.Entry.DecodedSize;
            }
        }

        SizeLabel = ArchiveTreeNodeViewModel.FormatByteSize(total);
        string[] sources = files
            .Select(static file => file.SourceArchiveName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        SourceLabel = sources.Length switch
        {
            0 => "—",
            1 => sources[0],
            _ => $"{sources.Length} layers"
        };
        DetailLine = $"{files.Count:N0} files · {SourceLabel}";
    }

    public string FolderPath { get; }
    public string SongName { get; }
    public IReadOnlyList<ArchiveTreeNodeViewModel> Files { get; }
    public string SizeLabel { get; }
    public string SourceLabel { get; }
    public override string Title => SongName;
    public override string Subtitle => FolderPath;
    public override string Badge => "song";
    public override string Meta => SizeLabel;
    public override string DetailLine { get; }
    public override string ToolTipText =>
        $"{FolderPath} · {Files.Count:N0} files";
}

public sealed class ArchiveFileRowViewModel : ArchiveBrowserRowViewModel
{
    public ArchiveFileRowViewModel(ArchiveTreeNodeViewModel node, bool inSongFolder)
    {
        Node = node;
        DetailLine = inSongFolder
            ? $"{Kind(node.Extension)} · {node.SourceArchiveName}"
            : node.SourceArchiveName;
    }

    public ArchiveTreeNodeViewModel Node { get; }
    public override string Title => Node.FileName;
    public override string Subtitle => Node.DirectoryName;
    public override string Badge =>
        string.IsNullOrWhiteSpace(Node.Extension) ? "file" : Node.Extension;
    public override string Meta => Node.SizeLabel;
    public override string DetailLine { get; }
    public override string ToolTipText => Node.DecodedPath;

    private static string Kind(string extension)
    {
        if (AudioExtensions.Contains(extension)) return "Audio";
        if (ImageExtensions.Contains(extension)) return "Image";
        if (VideoExtensions.Contains(extension)) return "Video";
        return "Chart";
    }

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ogg", ".wav", ".mp3", ".flac", ".aac", ".m4a", ".wma", ".opus"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds", ".gif", ".webp"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bik", ".mp4", ".avi", ".wmv", ".webm"
    };
}

internal static class SongFolderIndex
{
    public static IReadOnlyList<SongFolderRowViewModel> Group(
        IReadOnlyList<ArchiveTreeNodeViewModel> entries)
    {
        var groups = new Dictionary<string, List<ArchiveTreeNodeViewModel>>(
            StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ArchiveTreeNodeViewModel entry in entries)
        {
            if (!TryGetSongFolder(entry.DecodedPath, out string folderPath, out string songName))
            {
                continue;
            }

            if (!groups.TryGetValue(folderPath, out List<ArchiveTreeNodeViewModel>? files))
            {
                files = [];
                groups.Add(folderPath, files);
                names.Add(folderPath, songName);
            }

            files.Add(entry);
        }

        return groups
            .Select(pair => new SongFolderRowViewModel(
                pair.Key,
                names[pair.Key],
                pair.Value
                    .OrderBy(static file => file.FileName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static file => file.DecodedPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .OrderBy(static folder => folder.SongName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Technika keeps one song in pattern/<song-name>/<audio or chart>.
    private static bool TryGetSongFolder(
        string decodedPath,
        out string folderPath,
        out string songName)
    {
        folderPath = string.Empty;
        songName = string.Empty;
        string[] parts = decodedPath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3
            || !parts[0].Equals("pattern", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        songName = parts[1];
        folderPath = "pattern/" + songName;
        return true;
    }
}
