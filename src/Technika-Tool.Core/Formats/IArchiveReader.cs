using TechnikaTool.Core.Diagnostics;

namespace TechnikaTool.Core.Formats;

public interface IArchiveReader
{
    GameFamily Family { get; }

    ValueTask<ToolResult<ArchiveIndex>> IndexAsync(
        string archivePath,
        DetectionDepth depth,
        CancellationToken cancellationToken);
}
