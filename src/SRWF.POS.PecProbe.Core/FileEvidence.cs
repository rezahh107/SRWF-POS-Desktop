namespace SRWF.POS.PecProbe.Core;

public static class FileEvidence
{
    public static async Task<DirectorySnapshot> SnapshotDirectoryAsync(string role, string path, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(path))
            return new(role, path, false, false, DateTimeOffset.UtcNow, []);

        var files = new List<FileSnapshot>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                try
                {
                    var hash = await Hashing.Sha256FileAsync(file, cancellationToken);
                    info.Refresh();
                    files.Add(new(role, file, info.Name, info.Exists ? info.Length : 0, info.CreationTimeUtc, info.LastWriteTimeUtc, hash, true));
                }
                catch (Exception ex)
                {
                    info.Refresh();
                    files.Add(new(role, file, info.Name, info.Exists ? info.Length : 0, info.CreationTimeUtc, info.LastWriteTimeUtc, null, false, ex.Message));
                }
            }
            return new(role, path, true, true, DateTimeOffset.UtcNow, files);
        }
        catch (Exception ex)
        {
            return new(role, path, true, false, DateTimeOffset.UtcNow, files, ex.Message);
        }
    }

    public static async Task<ArtifactCapture?> TryCaptureAsync(string role, string path, int attempts = 8, int delayMilliseconds = 10, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, cancellationToken);
                var info = new FileInfo(path);
                var bytes = ms.ToArray();
                return new(role, path, Path.GetFileName(path), DateTimeOffset.UtcNow, info.CreationTimeUtc, info.LastWriteTimeUtc, bytes, Hashing.Sha256(bytes));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { break; }

            if (attempt + 1 < attempts) await Task.Delay(delayMilliseconds, cancellationToken);
        }
        return null;
    }
}
