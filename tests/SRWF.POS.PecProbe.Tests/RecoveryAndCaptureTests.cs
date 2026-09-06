using System.Text;
using System.Text.Json;
using SRWF.POS.PecProbe.Core;

namespace SRWF.POS.PecProbe.Tests;

public class RecoveryAndCaptureTests
{
    [Fact]
    public void MissingDiagnosticsRootDoesNotFabricateRecoveryBlock()
    {
        var root = Path.Combine(Path.GetTempPath(), "srwf-recovery-missing-" + Guid.NewGuid().ToString("N"));
        var result = SessionRecoveryClassifier.Assess(root);
        Assert.False(result.RecoveryRequired);
        Assert.Equal("NO_PRIOR_PUBLISH_EVIDENCE", result.Status);
    }

    [Fact]
    public void ObservationOnlySessionDoesNotFabricateRecoveryBlock()
    {
        using var sandbox = new TempDirectory("srwf-recovery-observe");
        var session = Directory.CreateDirectory(Path.Combine(sandbox.Path, "session-observe")).FullName;
        File.WriteAllText(Path.Combine(session, "session.json"), JsonSerializer.Serialize(new
        {
            sessionId = "session-observe",
            initialMode = "ObserveOnly",
            status = "OBSERVATION_STOPPED"
        }));
        WriteTimeline(session, Event(
            directoryRole: "Request",
            evidenceStatus: "REQUEST_EVENT_OBSERVED",
            fullPath: Path.Combine(sandbox.Path, "DISPATCH_STARTED.txt"),
            note: "REQUEST_FILE_PUBLISHED may appear in ordinary observation notes"));

        var result = SessionRecoveryClassifier.Assess(sandbox.Path);

        Assert.False(result.RecoveryRequired);
        Assert.Equal("NO_PRIOR_PUBLISH_EVIDENCE", result.Status);
    }

    [Fact]
    public void DurablePublishAttemptRestoresRecoveryBlockAcrossFreshAssessment()
    {
        using var sandbox = new TempDirectory("srwf-recovery-publish");
        var session = Directory.CreateDirectory(Path.Combine(sandbox.Path, "session-publish")).FullName;
        File.WriteAllText(Path.Combine(session, "publish-attempt.json"), JsonSerializer.Serialize(new
        {
            attemptId = "A-1",
            persistedBeforeDispatch = true,
            startedAtUtc = DateTimeOffset.UtcNow,
            automaticRetry = false
        }));

        var first = SessionRecoveryClassifier.Assess(sandbox.Path);
        var freshProcessEquivalent = SessionRecoveryClassifier.Assess(sandbox.Path);

        Assert.True(first.RecoveryRequired);
        Assert.True(freshProcessEquivalent.RecoveryRequired);
        Assert.Equal("RECOVERY_REQUIRED", freshProcessEquivalent.Status);
        Assert.Contains(freshProcessEquivalent.Findings, f => f.Status == "UNRESOLVED_DURABLE_PUBLISH_ATTEMPT");
    }

    [Fact]
    public void StructuredDispatchStartedTimelineEventRestoresRecoveryBlock()
    {
        using var sandbox = new TempDirectory("srwf-recovery-dispatch-started");
        var session = Directory.CreateDirectory(Path.Combine(sandbox.Path, "session-publish")).FullName;
        WriteTimeline(session, Event("Publish", "DISPATCH_STARTED"));

        var result = SessionRecoveryClassifier.Assess(sandbox.Path);

        Assert.True(result.RecoveryRequired);
        Assert.Equal("RECOVERY_REQUIRED", result.Status);
        Assert.Contains(result.Findings, f =>
            f.Status == "UNRESOLVED_DISPATCH_MARKER" &&
            f.Detail == "DISPATCH_STARTED");
    }

    [Fact]
    public void StructuredRequestFilePublishedTimelineEventRestoresRecoveryBlock()
    {
        using var sandbox = new TempDirectory("srwf-recovery-request-published");
        var session = Directory.CreateDirectory(Path.Combine(sandbox.Path, "session-publish")).FullName;
        WriteTimeline(session, Event("Publish", "REQUEST_FILE_PUBLISHED"));

        var result = SessionRecoveryClassifier.Assess(sandbox.Path);

        Assert.True(result.RecoveryRequired);
        Assert.Equal("RECOVERY_REQUIRED", result.Status);
        Assert.Contains(result.Findings, f =>
            f.Status == "UNRESOLVED_DISPATCH_MARKER" &&
            f.Detail == "REQUEST_FILE_PUBLISHED");
    }

    [Fact]
    public void DispatchMarkerTextInFullPathDoesNotFabricateRecoveryBlock()
    {
        using var sandbox = new TempDirectory("srwf-recovery-path-collision");
        var session = Directory.CreateDirectory(Path.Combine(sandbox.Path, "session-observe")).FullName;
        WriteTimeline(session, Event(
            directoryRole: "Request",
            evidenceStatus: "REQUEST_EVENT_OBSERVED",
            fullPath: Path.Combine(sandbox.Path, "DISPATCH_STARTED.txt")));

        var result = SessionRecoveryClassifier.Assess(sandbox.Path);

        Assert.False(result.RecoveryRequired);
        Assert.Equal("NO_PRIOR_PUBLISH_EVIDENCE", result.Status);
    }

    [Fact]
    public void DispatchMarkerTextInNoteDoesNotFabricateRecoveryBlock()
    {
        using var sandbox = new TempDirectory("srwf-recovery-note-collision");
        var session = Directory.CreateDirectory(Path.Combine(sandbox.Path, "session-observe")).FullName;
        WriteTimeline(session, Event(
            directoryRole: "Request",
            evidenceStatus: "REQUEST_EVENT_OBSERVED",
            note: "REQUEST_FILE_PUBLISHED"));

        var result = SessionRecoveryClassifier.Assess(sandbox.Path);

        Assert.False(result.RecoveryRequired);
        Assert.Equal("NO_PRIOR_PUBLISH_EVIDENCE", result.Status);
    }

    [Theory]
    [InlineData("NOT_DISPATCH_STARTED")]
    [InlineData("DISPATCH_STARTED_CANDIDATE")]
    [InlineData("REQUEST_FILE_PUBLISHED_NOTE")]
    public void SimilarButNonExactDispatchStatusDoesNotBlock(string evidenceStatus)
    {
        using var sandbox = new TempDirectory("srwf-recovery-nonexact");
        var session = Directory.CreateDirectory(Path.Combine(sandbox.Path, "session-publish-looking")).FullName;
        WriteTimeline(session, Event("Publish", evidenceStatus));

        var result = SessionRecoveryClassifier.Assess(sandbox.Path);

        Assert.False(result.RecoveryRequired);
        Assert.Equal("NO_PRIOR_PUBLISH_EVIDENCE", result.Status);
    }

    [Fact]
    public void MarkerStatusOutsidePublishRoleDoesNotBlock()
    {
        using var sandbox = new TempDirectory("srwf-recovery-role-boundary");
        var session = Directory.CreateDirectory(Path.Combine(sandbox.Path, "session-observe")).FullName;
        WriteTimeline(session, Event("Request", "DISPATCH_STARTED"));

        var result = SessionRecoveryClassifier.Assess(sandbox.Path);

        Assert.False(result.RecoveryRequired);
        Assert.Equal("NO_PRIOR_PUBLISH_EVIDENCE", result.Status);
    }

    [Fact]
    public void MalformedTimelineRecordAloneDoesNotFabricatePriorPublish()
    {
        using var sandbox = new TempDirectory("srwf-recovery-malformed");
        var session = Directory.CreateDirectory(Path.Combine(sandbox.Path, "session-observe")).FullName;
        File.WriteAllText(Path.Combine(session, "timeline.jsonl"), "{not-json" + Environment.NewLine);

        var result = SessionRecoveryClassifier.Assess(sandbox.Path);

        Assert.False(result.RecoveryRequired);
        Assert.Equal("NO_PRIOR_PUBLISH_EVIDENCE", result.Status);
    }

    [Fact]
    public void MalformedTimelineRecordDoesNotHideLaterStructuredDispatchEvidence()
    {
        using var sandbox = new TempDirectory("srwf-recovery-malformed-before-dispatch");
        var session = Directory.CreateDirectory(Path.Combine(sandbox.Path, "session-publish")).FullName;
        var timeline = Path.Combine(session, "timeline.jsonl");
        File.WriteAllText(timeline, "{not-json" + Environment.NewLine);
        File.AppendAllText(timeline, JsonSerializer.Serialize(Event("Publish", "DISPATCH_STARTED")) + Environment.NewLine);

        var result = SessionRecoveryClassifier.Assess(sandbox.Path);

        Assert.True(result.RecoveryRequired);
        Assert.Contains(result.Findings, f => f.Status == "UNRESOLVED_DISPATCH_MARKER" && f.Detail == "DISPATCH_STARTED");
    }

    [Fact]
    public void CorruptPublishAttemptFailsClosed()
    {
        using var sandbox = new TempDirectory("srwf-recovery-corrupt");
        var session = Directory.CreateDirectory(Path.Combine(sandbox.Path, "session-corrupt")).FullName;
        File.WriteAllText(Path.Combine(session, "publish-attempt.json"), "{not-json");

        var result = SessionRecoveryClassifier.Assess(sandbox.Path);

        Assert.True(result.RecoveryRequired);
        Assert.Contains(result.Findings, f => f.Status == "UNREADABLE_OR_CORRUPT_PUBLISH_EVIDENCE");
    }

    [Fact]
    public async Task StableFileRequiresRepeatedMatchingSamples()
    {
        using var sandbox = new TempDirectory("srwf-capture-stable");
        var path = Path.Combine(sandbox.Path, "TransAction.txt");
        var bytes = Encoding.ASCII.GetBytes("Amount=100\r\ntype=1\r\n");
        await File.WriteAllBytesAsync(path, bytes);

        var capture = await FileEvidence.TryCaptureAsync("Request", path, attempts: 4, delayMilliseconds: 1);

        Assert.NotNull(capture);
        Assert.Equal(CaptureStability.Stable, capture.Stability);
        Assert.True(capture.StabilityEstablished);
        Assert.True(capture.SuccessfulSampleCount >= 2);
        Assert.All(capture.Samples!, sample => Assert.True(sample.MetadataStableAcrossRead));
        Assert.All(capture.Samples!, sample => Assert.Equal(Hashing.Sha256(bytes), sample.Sha256));
    }

    [Fact]
    public async Task ChangingBytesBetweenSamplesCannotBecomeStableContractEvidence()
    {
        using var sandbox = new TempDirectory("srwf-capture-changing");
        var path = Path.Combine(sandbox.Path, "TransAction.txt");
        await File.WriteAllBytesAsync(path, Encoding.ASCII.GetBytes("Amount=100\r\n"));

        var changed = false;
        var capture = await FileEvidence.TryCaptureForTestAsync(
            "Request",
            path,
            attempts: 4,
            delayMilliseconds: 1,
            betweenSamples: async (sampleCount, file, _) =>
            {
                if (sampleCount == 1 && !changed)
                {
                    changed = true;
                    await File.WriteAllBytesAsync(file, Encoding.ASCII.GetBytes("Amount=200\r\n"));
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(1));
                }
            });

        Assert.NotNull(capture);
        Assert.NotEqual(CaptureStability.Stable, capture.Stability);
        Assert.False(capture.StabilityEstablished);
        Assert.True(capture.SuccessfulSampleCount >= 2);
        Assert.True(capture.Samples!.Select(s => s.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2);

        var contract = CandidateRequestAnalyzer.Analyze(capture);
        Assert.False(contract.CaptureStabilityEstablished);
        Assert.False(contract.HasReproducibleStructure);
    }

    [Fact]
    public async Task OneReadThenDisappearRetainsRawEvidenceButIsNotStable()
    {
        using var sandbox = new TempDirectory("srwf-capture-disappear");
        var path = Path.Combine(sandbox.Path, "TransAction.txt");
        var bytes = Encoding.ASCII.GetBytes("Amount=100\r\n");
        await File.WriteAllBytesAsync(path, bytes);

        var deleted = false;
        var capture = await FileEvidence.TryCaptureForTestAsync(
            "Request",
            path,
            attempts: 3,
            delayMilliseconds: 1,
            betweenSamples: (sampleCount, file, _) =>
            {
                if (sampleCount == 1 && !deleted)
                {
                    deleted = true;
                    File.Delete(file);
                }
                return Task.CompletedTask;
            });

        Assert.NotNull(capture);
        Assert.Equal(CaptureStability.DisappearedBeforeConfirmation, capture.Stability);
        Assert.False(capture.StabilityEstablished);
        Assert.Equal(Hashing.Sha256(bytes), capture.Sha256);
        Assert.NotEmpty(capture.Samples!);
    }

    private static FilesystemEventRecord Event(
        string directoryRole,
        string evidenceStatus,
        string? fullPath = null,
        string? note = null) => new(
            Sequence: 1,
            TimestampUtc: DateTimeOffset.UtcNow,
            DirectoryRole: directoryRole,
            EventType: "Created",
            FullPath: fullPath ?? Path.Combine(Path.GetTempPath(), "TransAction.txt"),
            FileName: Path.GetFileName(fullPath ?? "TransAction.txt"),
            EvidenceStatus: evidenceStatus,
            Note: note);

    private static void WriteTimeline(string sessionDirectory, params FilesystemEventRecord[] events)
    {
        File.WriteAllLines(
            Path.Combine(sessionDirectory, "timeline.jsonl"),
            events.Select(JsonSerializer.Serialize));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
