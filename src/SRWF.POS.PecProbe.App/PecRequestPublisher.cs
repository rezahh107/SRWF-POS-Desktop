using SRWF.POS.PecProbe.Core;

namespace SRWF.POS.PecProbe.App;

internal static class PecRequestPublisher
{
    public static async Task PublishAsync(
        string destinationPath,
        byte[] bytes,
        PublicationStrategy strategy,
        string? observedTemporaryFileName,
        bool unverifiedOverride,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(destinationPath) ?? throw new InvalidOperationException("Request destination has no directory.");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        if (File.Exists(destinationPath)) throw new IOException("RECOVERY_REQUIRED: Request destination already exists.");

        switch (strategy)
        {
            case PublicationStrategy.DirectWrite:
                await using (var stream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, true))
                {
                    await stream.WriteAsync(bytes, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                break;

            case PublicationStrategy.TempThenRename:
                string temp;
                if (!string.IsNullOrWhiteSpace(observedTemporaryFileName))
                {
                    temp = Path.Combine(directory, observedTemporaryFileName);
                }
                else if (unverifiedOverride)
                {
                    temp = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
                }
                else
                {
                    throw new InvalidOperationException("PUBLISH_CONTRACT_INCOMPLETE: temp-file naming/rename semantics were not observed.");
                }

                await SafeFilePublication.TempThenRenameAsync(temp, destinationPath, bytes, cancellationToken);
                break;

            case PublicationStrategy.OtherObserved:
            case PublicationStrategy.UnverifiedOverride:
            default:
                throw new InvalidOperationException("A concrete DIRECT_WRITE or TEMP_THEN_RENAME strategy must be selected before dispatch.");
        }
    }
}
