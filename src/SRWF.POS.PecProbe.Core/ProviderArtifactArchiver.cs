namespace SRWF.POS.PecProbe.Core;

public static class ProviderArtifactArchiver
{
    public static Task<ArchiveOperationResult> ArchiveAsync(
        string sourcePath,
        string archiveDirectory,
        DiagnosticCollector collector,
        CancellationToken cancellationToken = default) =>
        ArchiveCoreAsync(sourcePath, archiveDirectory, collector, null, cancellationToken);

    internal static async Task<ArchiveOperationResult> ArchiveCoreAsync(
        string sourcePath,
        string archiveDirectory,
        DiagnosticCollector collector,
        Func<CancellationToken, Task>? verificationBoundaryHook,
        CancellationToken cancellationToken = default)
    {
        collector.RecordEvent("Archive", "Archive", sourcePath, status: "PROVIDER_ARTIFACT_ARCHIVE_STARTED");
        try
        {
            if (!File.Exists(sourcePath))
            {
                collector.RecordEvent("Archive", "Archive", sourcePath, status: "PROVIDER_ARTIFACT_ARCHIVE_ABORTED", note: "Source no longer exists.");
                return new(sourcePath, "PROVIDER_ARTIFACT_ARCHIVE_ABORTED", null, null, "Source no longer exists.");
            }

            var captured = await FileEvidence.TryCaptureAsync("ProviderArtifact", sourcePath, cancellationToken: cancellationToken);
            if (captured is null)
            {
                collector.RecordEvent("Archive", "Archive", sourcePath, status: "PROVIDER_ARTIFACT_ARCHIVE_ABORTED", note: "Could not safely capture source bytes.");
                return new(sourcePath, "PROVIDER_ARTIFACT_ARCHIVE_ABORTED", null, null, "Could not safely capture source bytes.");
            }

            var stagingDir = Path.Combine(collector.SessionDirectory, "provider-archive-staging");
            Directory.CreateDirectory(stagingDir);
            var stagedPath = Path.Combine(stagingDir, $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Path.GetFileName(sourcePath)}");
            await File.WriteAllBytesAsync(stagedPath, captured.RawBytes, cancellationToken);

            var persistedHash = Hashing.Sha256(await File.ReadAllBytesAsync(stagedPath, cancellationToken));
            if (!string.Equals(persistedHash, captured.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                collector.RecordEvent("Archive", "Archive", sourcePath, status: "PROVIDER_ARTIFACT_ARCHIVE_ABORTED", note: "Persisted evidence hash mismatch.");
                return new(sourcePath, "PROVIDER_ARTIFACT_ARCHIVE_ABORTED", null, captured.Sha256, "Persisted evidence hash mismatch.");
            }

            if (verificationBoundaryHook is not null)
                await verificationBoundaryHook(cancellationToken);

            var sourceHashNow = await Hashing.Sha256FileAsync(sourcePath, cancellationToken);
            if (!string.Equals(sourceHashNow, captured.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                collector.RecordEvent("Archive", "Archive", sourcePath, status: "PROVIDER_ARTIFACT_ARCHIVE_ABORTED", note: "Source changed after capture.");
                return new(sourcePath, "PROVIDER_ARTIFACT_ARCHIVE_ABORTED", null, captured.Sha256, "Source changed after capture.");
            }

            Directory.CreateDirectory(archiveDirectory);
            var archivePath = Path.Combine(archiveDirectory, $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Path.GetFileName(sourcePath)}");
            File.Move(sourcePath, archivePath, overwrite: false);
            collector.RecordEvent("Archive", "Archive", sourcePath, status: "PROVIDER_ARTIFACT_ARCHIVED", note: archivePath);
            return new(sourcePath, "PROVIDER_ARTIFACT_ARCHIVED", archivePath, captured.Sha256);
        }
        catch (Exception ex)
        {
            collector.RecordError("PROVIDER_ARTIFACT_ARCHIVE_ABORTED", ex);
            collector.RecordEvent("Archive", "Archive", sourcePath, status: "PROVIDER_ARTIFACT_ARCHIVE_ABORTED", note: ex.Message);
            return new(sourcePath, "PROVIDER_ARTIFACT_ARCHIVE_ABORTED", null, null, ex.Message);
        }
    }
}
