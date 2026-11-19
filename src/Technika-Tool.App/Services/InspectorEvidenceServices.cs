using TechnikaTool.Core.Diagnostics;
using TechnikaTool.Core.Evidence;
using TechnikaTool.Core.Formats;
using TechnikaTool.Core.T2;
using TechnikaTool.Core.T3;

namespace TechnikaTool.App.Services;

internal interface IInspectorEvidenceService
{
    ValueTask<ToolResult<ClientEvidence>> LoadT3Async(string clientPath, CancellationToken cancellationToken);
}

internal sealed class InspectorEvidenceService : IInspectorEvidenceService
{
    private readonly T3ClientEvidenceProvider provider = new();

    public ValueTask<ToolResult<ClientEvidence>> LoadT3Async(string clientPath, CancellationToken cancellationToken) =>
        provider.LoadAsync(clientPath, cancellationToken);
}

internal interface IArchiveCatalogService
{
    IAsyncEnumerable<CatalogItem> ScanAsync(
        ClientEvidence evidence,
        string? pakKeyDirectory,
        IReadOnlyList<string> archives,
        CancellationToken cancellationToken);
}

internal sealed class ArchiveCatalogService : IArchiveCatalogService
{
    public IAsyncEnumerable<CatalogItem> ScanAsync(
        ClientEvidence evidence,
        string? pakKeyDirectory,
        IReadOnlyList<string> archives,
        CancellationToken cancellationToken)
    {
        IArchiveReader reader = evidence.Family == GameFamily.Technika2
            ? new T2ArchiveReader(evidence)
            : new T3ArchiveReader(
                evidence,
                pakKeyDirectory ?? string.Empty,
                preferNamedKey: true);
        return new ArchiveCatalog(new ArchiveDetector(reader)).ScanAsync(archives, cancellationToken);
    }
}
