namespace SRWF.POS.PecProbe.Core;

public static class SafeFilePublication
{
    public static Task TempThenRenameAsync(
        string temporaryPath,
        string destinationPath,
        byte[] bytes,
        CancellationToken cancellationToken = default) =>
        TempThenRenameCoreAsync(temporaryPath, destinationPath, bytes, null, cancellationToken);

    internal static async Task TempThenRenameCoreAsync(
        string temporaryPath,
        string destinationPath,
        byte[] bytes,
        Func<string, string, CancellationToken, Task>? beforeMove,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(temporaryPath))
            throw new IOException("RECOVERY_REQUIRED: temporary Request path already exists.");
        if (File.Exists(destinationPath))
            throw new IOException("RECOVERY_REQUIRED: Request destination already exists.");

        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous))
        {
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        if (beforeMove is not null)
            await beforeMove(temporaryPath, destinationPath, cancellationToken);

        // Deliberately no catch/delete cleanup here. After the stream is closed, pathname ownership
        // can no longer be proven if a race or external replacement occurs. Any failure propagates
        // and the provider-directory path is left untouched for recovery/evidence handling.
        File.Move(temporaryPath, destinationPath, overwrite: false);
    }
}
