using System.IO.Compression;
using System.Text;
using SRWF.POS.PecProbe.Core;

namespace SRWF.POS.PecProbe.Tests;

public class ObservationDiagnosticsAndArchiveTests
{
    [Fact]
    public async Task ObserveReadPathDoesNotModifyProviderDirectory()
    {
        using var fixture = new Fixture();
        var file = Path.Combine(fixture.Request, "TransAction.txt");
        await File.WriteAllTextAsync(file, "Amount=1\n");
        var before = await File.ReadAllBytesAsync(file);
        var beforeWrite = File.GetLastWriteTimeUtc(file);

        _ = await FileEvidence.SnapshotDirectoryAsync("Request", fixture.Request);
        _ = await FileEvidence.TryCaptureAsync("Request", file);

        Assert.Equal(before, await File.ReadAllBytesAsync(file));
        Assert.Equal(beforeWrite, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public async Task DirectWriteCanBeCapturedWithSharedReadSemantics()
    {
        using var fixture = new Fixture();
        var file = Path.Combine(fixture.Request, "TransAction.txt");
        var bytes = Encoding.ASCII.GetBytes("Amount=10\r\n");
        await File.WriteAllBytesAsync(file, bytes);
        var capture = await FileEvidence.TryCaptureAsync("Request", file);
        Assert.NotNull(capture);
        Assert.Equal(bytes, capture!.RawBytes);
    }

    [Fact]
    public void MissedTransientContentIsRecordedTruthfully()
    {
        using var fixture = new Fixture();
        var collector = fixture.Collector();
        var missing = Path.Combine(fixture.Request, "gone.txt");
        collector.RecordRequestMiss(missing);
        var last = Assert.Single(collector.Events);
        Assert.Equal("REQUEST_CONTENT_NOT_CAPTURED", last.EvidenceStatus);
    }

    [Fact]
    public async Task SafeBundleExcludesRawResponseByDefault()
    {
        using var fixture = new Fixture();
        var collector = fixture.Collector();
        var raw = Encoding.ASCII.GetBytes("RESULT CODE 00 4111111111111111\r\n");
        collector.RecordResponseCapture(new("Response", Path.Combine(fixture.Response, "TransAction.txt"), "TransAction.txt",
            DateTimeOffset.UtcNow, DateTime.UtcNow, DateTime.UtcNow, raw, Hashing.Sha256(raw)));
        collector.Complete("TEST");

        var zip = DiagnosticExporter.ExportSafeBundle(collector.SessionDirectory);
        using var archive = ZipFile.OpenRead(zip);
        Assert.DoesNotContain(archive.Entries, e => e.FullName.Equals("observed-response.raw", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(archive.Entries, e => e.FullName.Equals("observed-response-sanitized.txt", StringComparison.OrdinalIgnoreCase));
        var entry = archive.GetEntry("observed-response-sanitized.txt")!;
        using var reader = new StreamReader(entry.Open());
        var text = await reader.ReadToEndAsync();
        Assert.DoesNotContain("4111111111111111", text);
    }

    [Fact]
    public async Task ArchivePersistsEvidenceThenMovesUnchangedSource()
    {
        using var fixture = new Fixture();
        var collector = fixture.Collector();
        var source = Path.Combine(fixture.Response, "TransAction.txt");
        await File.WriteAllTextAsync(source, "RESULT CODE 00");
        var result = await ProviderArtifactArchiver.ArchiveAsync(source, Path.Combine(collector.SessionDirectory, "provider-archive"), collector);
        Assert.Equal("PROVIDER_ARTIFACT_ARCHIVED", result.Status);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(result.ArchivePath));
        Assert.Contains(collector.Events, e => e.EvidenceStatus == "PROVIDER_ARTIFACT_ARCHIVE_STARTED");
        Assert.Contains(collector.Events, e => e.EvidenceStatus == "PROVIDER_ARTIFACT_ARCHIVED");
    }

    [Fact]
    public async Task ChangedSourceHashAbortsArchiveAndLeavesProviderArtifact()
    {
        using var fixture = new Fixture();
        var collector = fixture.Collector();
        var source = Path.Combine(fixture.Response, "TransAction.txt");
        await File.WriteAllTextAsync(source, "first");

        var result = await ProviderArtifactArchiver.ArchiveCoreAsync(
            source,
            Path.Combine(collector.SessionDirectory, "provider-archive"),
            collector,
            async ct => await File.WriteAllTextAsync(source, "changed", ct));

        Assert.Equal("PROVIDER_ARTIFACT_ARCHIVE_ABORTED", result.Status);
        Assert.True(File.Exists(source));
        Assert.Equal("changed", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task FailedArchiveDestinationLeavesProviderArtifactIntact()
    {
        using var fixture = new Fixture();
        var collector = fixture.Collector();
        var source = Path.Combine(fixture.Response, "TransAction.txt");
        await File.WriteAllTextAsync(source, "evidence");
        var invalidArchiveDir = Path.Combine(fixture.Root, "not-a-directory");
        await File.WriteAllTextAsync(invalidArchiveDir, "file blocks directory creation");

        var result = await ProviderArtifactArchiver.ArchiveAsync(source, invalidArchiveDir, collector);

        Assert.Equal("PROVIDER_ARTIFACT_ARCHIVE_ABORTED", result.Status);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task FakeSimulatorNeverUsesRealPecPathAndSupportsAllScenarios()
    {
        foreach (var scenario in Enum.GetValues<FakePecScenario>())
        {
            using var sandbox = FakePecSandbox.Create();
            Assert.True(Path.GetFullPath(sandbox.Root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase));
            var simulator = new FakePecSimulator(sandbox);
            await simulator.RunAsync(scenario);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "srwf-probe-tests", Guid.NewGuid().ToString("N"));
        public string ProviderBase { get; }
        public string Request { get; }
        public string Response { get; }
        public string Diagnostics { get; } = Path.Combine(Path.GetTempPath(), "srwf-probe-diagnostics-tests", Guid.NewGuid().ToString("N"));

        public Fixture()
        {
            ProviderBase = Path.Combine(Root, "pec");
            Request = Path.Combine(ProviderBase, "Request");
            Response = Path.Combine(ProviderBase, "Response");
            Directory.CreateDirectory(Request);
            Directory.CreateDirectory(Response);
            Directory.CreateDirectory(Diagnostics);
        }

        public DiagnosticCollector Collector()
        {
            var config = new ProbeConfiguration(ProviderBase, Request, Response, Diagnostics);
            return new(config, new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                "IMPLEMENTED_AGAINST_EXTERNAL_EVIDENCE", "PENDING_REAL_PEC_VALIDATION", ProbeMode.ObserveOnly,
                new(null, TesterDisplayedUnit.Unknown)));
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
            try { if (Directory.Exists(Diagnostics)) Directory.Delete(Diagnostics, true); } catch { }
        }
    }
}
