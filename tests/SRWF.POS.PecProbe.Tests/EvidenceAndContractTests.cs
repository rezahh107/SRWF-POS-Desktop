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
    }

    [Fact]
    public void DistinguishesLfCrAndCrLf()
    {
        var lf = EvidenceTextAnalyzer.Analyze(Encoding.ASCII.GetBytes("a=1\nb=2\n"));
        var cr = EvidenceTextAnalyzer.Analyze(Encoding.ASCII.GetBytes("a=1\rb=2\r"));
        var crlf = EvidenceTextAnalyzer.Analyze(Encoding.ASCII.GetBytes("a=1\r\nb=2\r\n"));
        Assert.Equal("LF", lf.NewlineRepresentation);
        Assert.Equal("CR", cr.NewlineRepresentation);
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
        var contract = Contract(bytes);

        var rendered = RequestTemplateRenderer.Render(contract, 456);
        var text = Encoding.ASCII.GetString(rendered.Bytes);

        Assert.Contains("Amount=456", text);
        Assert.Contains("unknown=KEEP", text);
        Assert.DoesNotContain("4560", text);
        Assert.Equal("STRUCTURALLY_EQUIVALENT",
            RequestComparer.Compare(bytes, rendered.Bytes, rendered.ReplacedFields).Result);
    }

    [Fact]
    public void DynamicRequestIsStructuralNotByteIdenticalOnlyWhenFieldIsExplicitlyAllowed()
    {
        var observed = Encoding.ASCII.GetBytes("Amount=100\r\ntype=1\r\n");
        var prepared = Encoding.ASCII.GetBytes("Amount=200\r\ntype=1\r\n");

        var allowed = RequestComparer.Compare(observed, prepared, new[] { "Amount" });
        var notAllowed = RequestComparer.Compare(observed, prepared);

        Assert.Equal("STRUCTURALLY_EQUIVALENT", allowed.Result);
        Assert.False(allowed.ByteIdentical);
        Assert.Equal("DIFFERENT", notAllowed.Result);
    }

    [Theory]
    [InlineData("\r\n", true)]
    [InlineData("\r\n", false)]
    [InlineData("\n", true)]
    [InlineData("\n", false)]
    [InlineData("\r", true)]
    [InlineData("\r", false)]
    public void RendererPreservesExactDelimiterAndFinalNewline(string delimiter, bool finalNewline)
    {
        var text = "unknown=KEEP" + delimiter + "Amount=100" + delimiter + "type=X" + (finalNewline ? delimiter : string.Empty);
        var bytes = Encoding.UTF8.GetBytes(text);
        var contract = Contract(bytes);

        var rendered = RequestTemplateRenderer.Render(contract, 200, new Dictionary<string, string> { ["type"] = "Y" });
        var expected = "unknown=KEEP" + delimiter + "Amount=200" + delimiter + "type=Y" + (finalNewline ? delimiter : string.Empty);

        Assert.Equal(expected, Encoding.UTF8.GetString(rendered.Bytes));
        var comparison = RequestComparer.Compare(bytes, rendered.Bytes, rendered.ReplacedFields);
        Assert.Equal("STRUCTURALLY_EQUIVALENT", comparison.Result);
        Assert.True(comparison.NewlineSame);
        Assert.True(comparison.FinalNewlineSame);
        Assert.True(comparison.FieldOrderSame);
    }

    [Fact]
    public void RendererPreservesMixedDelimiterSequenceExactly()
    {
        var observedText = "Amount=100\r\ntype=1\nunknown=KEEP\rIP=127.0.0.1";
        var observed = Encoding.ASCII.GetBytes(observedText);
        var contract = Contract(observed);

        var rendered = RequestTemplateRenderer.Render(contract, 200);
        var renderedText = Encoding.ASCII.GetString(rendered.Bytes);

        Assert.Equal("Amount=200\r\ntype=1\nunknown=KEEP\rIP=127.0.0.1", renderedText);
        Assert.Equal("STRUCTURALLY_EQUIVALENT",
            RequestComparer.Compare(observed, rendered.Bytes, rendered.ReplacedFields).Result);
    }

    [Theory]
    [InlineData("UTF8_BOM")]
    [InlineData("UTF16_LE")]
    [InlineData("UTF16_BE")]
    public void RendererPreservesObservedEncodingAndBom(string kind)
    {
        var text = "Amount=100\r\nunknown=KEEP\r\n";
        var observed = kind switch
        {
            "UTF8_BOM" => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray(),
            "UTF16_LE" => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray(),
            "UTF16_BE" => Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var contract = Contract(observed);

        var rendered = RequestTemplateRenderer.Render(contract, 200);
        var comparison = RequestComparer.Compare(observed, rendered.Bytes, rendered.ReplacedFields);

        Assert.Equal("STRUCTURALLY_EQUIVALENT", comparison.Result);
        Assert.True(comparison.EncodingSame);
        Assert.True(comparison.BomSame);
        Assert.True(EvidenceTextAnalyzer.Analyze(rendered.Bytes).BomPresent);
    }

    [Fact]
    public void ComparerRejectsChangedDelimiterEvenWhenDynamicFieldIsAllowed()
    {
        var observed = Encoding.ASCII.GetBytes("Amount=100\r\nunknown=KEEP\r\n");
        var prepared = Encoding.ASCII.GetBytes("Amount=200\nunknown=KEEP\r\n");

        var result = RequestComparer.Compare(observed, prepared, new[] { "Amount" });

        Assert.Equal("DIFFERENT", result.Result);
        Assert.False(result.NewlineSame);
    }

    [Fact]
    public void ComparerRejectsUnknownFieldMutation()
    {
        var observed = Encoding.ASCII.GetBytes("Amount=100\r\nunknown=KEEP\r\n");
        var prepared = Encoding.ASCII.GetBytes("Amount=200\r\nunknown=CHANGED\r\n");

        var result = RequestComparer.Compare(observed, prepared, new[] { "Amount" });

        Assert.Equal("DIFFERENT", result.Result);
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

    [Fact]
    public void ChangedOnlyActivityDoesNotProveDirectCreate()
    {
        var path = Path.Combine(Path.GetTempPath(), "TransAction.txt");
        var events = new[]
        {
            new FilesystemEventRecord(1, DateTimeOffset.UtcNow, "Request", "Changed", path, "TransAction.txt")
        };

        var result = PublicationPatternInferer.Infer(events, path, false);

        Assert.Equal(PublicationPattern.OtherObservedPattern, result.Pattern);
        Assert.Equal("LOW", result.Confidence);
    }

    private static ObservedRequestContract Contract(byte[] bytes)
    {
        var now = DateTime.UtcNow;
        var capture = new ArtifactCapture(
            "Request", Path.Combine(Path.GetTempPath(), "TransAction.txt"), "TransAction.txt", DateTimeOffset.UtcNow,
            now, now, bytes, Hashing.Sha256(bytes), CaptureStatus.ContentCaptured, CaptureStability.Stable, 2,
            [
                new ArtifactReadSample(1, DateTimeOffset.UtcNow, now, now, bytes.Length, now, now, bytes.Length, bytes, Hashing.Sha256(bytes), true),
                new ArtifactReadSample(2, DateTimeOffset.UtcNow, now, now, bytes.Length, now, now, bytes.Length, bytes, Hashing.Sha256(bytes), true)
            ]);
        return CandidateRequestAnalyzer.Analyze(capture) with
        {
            PublicationPattern = PublicationPattern.DirectCreateAndWrite,
            PublicationStrategyEvidence = "test",
            PublicationStrategyConfidence = "HIGH",
            PublicationEvidenceEstablished = true
        };
    }
}
