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

    public static Task<ArtifactCapture?> TryCaptureAsync(
        string role,
        string path,
        int attempts = 8,
        int delayMilliseconds = 10,
        CancellationToken cancellationToken = default) =>
        TryCaptureCoreAsync(role, path, attempts, delayMilliseconds, null, cancellationToken);

    internal static Task<ArtifactCapture?> TryCaptureForTestAsync(
        string role,
        string path,
        int attempts,
        int delayMilliseconds,
        Func<int, string, CancellationToken, Task>? betweenSamples,
        CancellationToken cancellationToken = default) =>
        TryCaptureCoreAsync(role, path, attempts, delayMilliseconds, betweenSamples, cancellationToken);

    private static async Task<ArtifactCapture?> TryCaptureCoreAsync(
        string role,
        string path,
        int attempts,
        int delayMilliseconds,
        Func<int, string, CancellationToken, Task>? betweenSamples,
        CancellationToken cancellationToken)
    {
        if (attempts < 2) attempts = 2;
        if (delayMilliseconds < 0) delayMilliseconds = 0;

        var samples = new List<ArtifactReadSample>();
        ArtifactReadSample? reference = null;
        var sawConflict = false;
        var disappearedAfterSample = false;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArtifactReadSample? sample = null;
            try
            {
                sample = await ReadSampleAsync(path, samples.Count + 1, cancellationToken);
            }
            catch (FileNotFoundException)
            {
                if (samples.Count > 0) disappearedAfterSample = true;
            }
            catch (DirectoryNotFoundException)
            {
                if (samples.Count > 0) disappearedAfterSample = true;
            }
            catch (IOException)
            {
                // A transient sharing/consumption race is evidence uncertainty, not proof of absence.
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }

            if (sample is not null)
            {
                samples.Add(sample);
                if (sample.MetadataStableAcrossRead)
                {
                    if (reference is null)
                    {
                        reference = sample;
                    }
                    else if (EquivalentStableSamples(reference, sample))
                    {
                        if (!sawConflict && !disappearedAfterSample)
                            return BuildCapture(role, path, sample, samples, CaptureStability.Stable);
                    }
                    else
                    {
                        sawConflict = true;
                    }
                }
            }

            if (betweenSamples is not null && samples.Count > 0)
                await betweenSamples(samples.Count, path, cancellationToken);

            if (attempt + 1 < attempts && delayMilliseconds > 0)
                await Task.Delay(delayMilliseconds, cancellationToken);
        }

        if (samples.Count == 0)
            return null;

        var latest = samples[^1];
        var stability = sawConflict
            ? CaptureStability.Unstable
            : disappearedAfterSample
                ? CaptureStability.DisappearedBeforeConfirmation
                : CaptureStability.NotEstablished;
        return BuildCapture(role, path, latest, samples, stability);
    }

    private static async Task<ArtifactReadSample> ReadSampleAsync(string path, int sampleNumber, CancellationToken cancellationToken)
    {
        var before = new FileInfo(path);
        before.Refresh();
        if (!before.Exists) throw new FileNotFoundException("Candidate artifact disappeared before sampling.", path);

        var creationBefore = before.CreationTimeUtc;
        var lastWriteBefore = before.LastWriteTimeUtc;
        var lengthBefore = before.Length;

        byte[] bytes;
        await using (var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous))
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, cancellationToken);
            bytes = ms.ToArray();
        }

        var after = new FileInfo(path);
        after.Refresh();
        var existsAfter = after.Exists;
        var creationAfter = existsAfter ? after.CreationTimeUtc : DateTime.MinValue;
        var lastWriteAfter = existsAfter ? after.LastWriteTimeUtc : DateTime.MinValue;
        var lengthAfter = existsAfter ? after.Length : -1;
        var metadataStable = existsAfter &&
                             creationBefore == creationAfter &&
                             lastWriteBefore == lastWriteAfter &&
                             lengthBefore == lengthAfter &&
                             bytes.LongLength == lengthAfter;

        return new(
            sampleNumber,
            DateTimeOffset.UtcNow,
            creationBefore,
            lastWriteBefore,
            lengthBefore,
            creationAfter,
            lastWriteAfter,
            lengthAfter,
            bytes,
            Hashing.Sha256(bytes),
            metadataStable);
    }

    private static bool EquivalentStableSamples(ArtifactReadSample left, ArtifactReadSample right) =>
        left.MetadataStableAcrossRead &&
        right.MetadataStableAcrossRead &&
        left.LengthAfter == right.LengthAfter &&
        left.CreationTimeUtcAfter == right.CreationTimeUtcAfter &&
        left.LastWriteTimeUtcAfter == right.LastWriteTimeUtcAfter &&
        string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase) &&
        left.RawBytes.AsSpan().SequenceEqual(right.RawBytes);

    private static ArtifactCapture BuildCapture(
        string role,
        string path,
        ArtifactReadSample latest,
        IReadOnlyList<ArtifactReadSample> samples,
        CaptureStability stability) => new(
            role,
            path,
            Path.GetFileName(path),
            latest.SampledAtUtc,
            latest.CreationTimeUtcAfter == DateTime.MinValue ? latest.CreationTimeUtcBefore : latest.CreationTimeUtcAfter,
            latest.LastWriteTimeUtcAfter == DateTime.MinValue ? latest.LastWriteTimeUtcBefore : latest.LastWriteTimeUtcAfter,
            latest.RawBytes,
            latest.Sha256,
            CaptureStatus.ContentCaptured,
            stability,
            samples.Count,
            samples.ToArray());
}
