using System.Text;
using SRWF.POS.PecProbe.Core;

namespace SRWF.POS.PecProbe.Tests;

public class EvidenceAndContractTests
{
    [Fact]
    public void DetectsUtf8BomAndCrLfWithoutNormalizingRawBytes()
    {
        var payload = Encoding.UTF8.GetBytes("Amount=10\r\ntype=1\r\nIP=1.2.3.4\r\nport=5\r\n");
        var raw = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(payload).ToArray();

        var evidence = EvidenceTextAnalyzer.Analyze(raw);

        Assert.Equal("UTF-8", evidence.ProbableEncoding);
        Assert.True(evidence.BomPresent);
        Assert.Equal("CRLF", evidence.NewlineRepresentation);
        Assert.True(evidence.FinalNewlinePresent);
        Assert.Equal(Hashing.Sha256(raw), Hashing.Sha256(raw));
    }

    [Fact]
    public void DistinguishesLfFromCrLf()
    {
        var lf = EvidenceTextAnalyzer.Analyze(Encoding.ASCII.GetBytes("a=1\nb=2\n"));
        var crlf = EvidenceTextAnalyzer.Analyze(Encoding.ASCII.GetBytes("a=1\r\nb=2\r\n"));
        Assert.Equal("LF", lf.NewlineRepresentation);
        Assert.Equal("CRLF", crlf.NewlineRepresentation);
    }

    [Fact]
    public void PreservesFieldOrder()
    {
        var evidence = EvidenceTextAnalyzer.Analyze(Encoding.ASCII.GetBytes("z=1\r\nAmount=2\r\ntype=3\r\n"));
        Assert.Equal(new[] { "z", "Amount", "type" }, evidence.FieldNames);
    }

    [Fact]
    public void CandidateSchemaRequiresObservedExactCandidateOrder()
    {
        var matching = EvidenceTextAnalyzer.Analyze(Encoding.ASCII.GetBytes("Amount=1\r\ntype=2\r\nIP=3\r\nport=4\r\n"));
        var different = EvidenceTextAnalyzer.Analyze(Encoding.ASCII.GetBytes("type=2\r\nAmount=1\r\nIP=3\r\nport=4\r\n"));
        Assert.Equal(CandidateSchemaMatch.MatchesExternalCandidate, CandidateRequestAnalyzer.ClassifySchema(matching));
        Assert.Equal(CandidateSchemaMatch.DiffersFromExternalCandidate, CandidateRequestAnalyzer.ClassifySchema(different));
    }

    [Fact]
    public void AmountRatioIsObservedButUnitRemainsUnproven()
    {
        var result = AmountRelationAnalyzer.Analyze(new(1000, TesterDisplayedUnit.Toman), 10000);
        Assert.Equal("AMOUNT_RELATION_OBSERVED", result.Status);
        Assert.Equal(AmountRelationship.RequestEqualsTesterTimes10, result.Relationship);
        Assert.Equal("AMOUNT_UNIT_NOT_PROVEN", result.ProofStatus);
    }

    [Fact]
    public void RequestRendererChangesRawAmountOnlyWithoutUnitConversion()
    {
        var bytes = Encoding.ASCII.GetBytes("Amount=123\r\ntype=7\r\nIP=10.0.0.2\r\nport=9000\r\nunknown=KEEP\r\n");
        var capture = Capture("TransAction.txt", bytes);
        var contract = CandidateRequestAnalyzer.Analyze(capture) with
        {
            PublicationPattern = PublicationPattern.DirectCreateAndWrite,
            PublicationStrategyEvidence = "test",
            PublicationStrategyConfidence = "HIGH"
        };

        var rendered = RequestTemplateRenderer.Render(contract, 456);
        var text = Encoding.ASCII.GetString(rendered.Bytes);

        Assert.Contains("Amount=456", text);
        Assert.Contains("unknown=KEEP", text);
        Assert.DoesNotContain("4560", text);
    }

    [Fact]
    public void DynamicRequestIsStructuralNotByteIdentical()
    {
        var observed = Encoding.ASCII.GetBytes("Amount=100\r\ntype=1\r\n");
        var prepared = Encoding.ASCII.GetBytes("Amount=200\r\ntype=1\r\n");
        var result = RequestComparer.Compare(observed, prepared);
        Assert.Equal("STRUCTURALLY_EQUIVALENT", result.Result);
        Assert.False(result.ByteIdentical);
    }

    [Fact]
    public void TempRenameInferenceRecordsPattern()
    {
        var events = new[]
        {
            new FilesystemEventRecord(1, DateTimeOffset.UtcNow, "Request", "Created", @"C:\x\tx.tmp", "tx.tmp"),
            new FilesystemEventRecord(2, DateTimeOffset.UtcNow, "Request", "Renamed", @"C:\x\TransAction.txt", "TransAction.txt", @"C:\x\tx.tmp")
        };
        var result = PublicationPatternInferer.Infer(events, @"C:\x\TransAction.txt", false);
        Assert.Equal(PublicationPattern.TempFileThenRename, result.Pattern);
        Assert.Equal("HIGH", result.Confidence);
    }

    private static ArtifactCapture Capture(string name, byte[] bytes) => new(
        "Request", Path.Combine(Path.GetTempPath(), name), name, DateTimeOffset.UtcNow,
        DateTime.UtcNow, DateTime.UtcNow, bytes, Hashing.Sha256(bytes));
}
