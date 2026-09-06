using System.Text;
using SRWF.POS.PecProbe.Core;

namespace SRWF.POS.PecProbe.Tests;

public class PublishSafetyTests
{
    [Fact]
    public void NoObservedContractBlocksNormalPublish()
    {
        var result = PublishGate.Evaluate(new(null, 100, 100000, false, false, false, true, true, false));
        Assert.False(result.Allowed);
        Assert.Contains("NO_OBSERVED_TESTER_REQUEST", result.Reasons);
    }

    [Fact]
    public void IncompleteContractBlocksNormalPublish()
    {
        var contract = Contract(PublicationPattern.Unknown, publicationEvidence: false);
        var result = PublishGate.Evaluate(Input(contract, comparison: EquivalentComparison(contract)));
        Assert.False(result.Allowed);
        Assert.Contains("PUBLISH_CONTRACT_INCOMPLETE", result.Reasons);
    }

    [Fact]
    public void ExplicitOverrideRequiresExplicitStrategyAndIsLoggedByStatus()
    {
        var blocked = PublishGate.Evaluate(new(null, 100, 100000, false, false, false, true, true, true));
        Assert.False(blocked.Allowed);
        Assert.Contains("UNVERIFIED_OVERRIDE_STRATEGY_REQUIRED", blocked.Reasons);

        var allowed = PublishGate.Evaluate(new(null, 100, 100000, false, false, false, true, true, true, PublicationStrategy.DirectWrite));
        Assert.True(allowed.Allowed);
        Assert.Equal("UNVERIFIED_PUBLISH_OVERRIDE", allowed.Status);
    }

    [Fact]
    public void UnresolvedPriorAttemptBlocksEvenExplicitOverride()
    {
        var result = PublishGate.Evaluate(new(
            null, 100, 100000, false, false, false, true, true, true,
            PublicationStrategy.DirectWrite,
            UnresolvedPriorAttempt: true));

        Assert.False(result.Allowed);
        Assert.Equal("RECOVERY_REQUIRED", result.Status);
        Assert.Contains("RECOVERY_REQUIRED_UNRESOLVED_PRIOR_ATTEMPT", result.Reasons);
    }

    [Fact]
    public void RawAmountOverHardCeilingIsRejected()
    {
        var contract = Contract(PublicationPattern.DirectCreateAndWrite);
        var result = PublishGate.Evaluate(Input(contract, rawAmount: 100001, comparison: EquivalentComparison(contract)));
        Assert.False(result.Allowed);
        Assert.Contains("RAW_AMOUNT_EXCEEDS_MAX_PROBE_RAW_AMOUNT", result.Reasons);
    }

    [Fact]
    public void StaleRequestBlocksDispatch()
    {
        var contract = Contract(PublicationPattern.DirectCreateAndWrite);
        var result = PublishGate.Evaluate(Input(contract, staleRequest: true, comparison: EquivalentComparison(contract)));
        Assert.False(result.Allowed);
        Assert.Equal("RECOVERY_REQUIRED", result.Status);
    }

    [Fact]
    public void StaleResponseBlocksDispatch()
    {
        var contract = Contract(PublicationPattern.DirectCreateAndWrite);
        var result = PublishGate.Evaluate(Input(contract, staleResponse: true, comparison: EquivalentComparison(contract)));
        Assert.False(result.Allowed);
        Assert.Equal("RECOVERY_REQUIRED", result.Status);
    }

    [Fact]
    public void UnresolvedUnitNeedsAdditionalConfirmation()
    {
        var contract = Contract(PublicationPattern.DirectCreateAndWrite);
        var result = PublishGate.Evaluate(Input(contract, additionalUnitConfirmation: false, comparison: EquivalentComparison(contract)));
        Assert.False(result.Allowed);
        Assert.Contains("UNRESOLVED_AMOUNT_UNIT_CONFIRMATION_REQUIRED", result.Reasons);
    }

    [Fact]
    public void TempRenameWithoutObservedTempNameIsIncomplete()
    {
        var contract = Contract(PublicationPattern.TempFileThenRename) with { ObservedTemporaryFileName = null };
        Assert.False(contract.HasReproducibleStructure);
    }

    [Fact]
    public void UnstableCaptureBlocksNormalPublish()
    {
        var contract = Contract(PublicationPattern.DirectCreateAndWrite) with
        {
            CaptureStabilityEstablished = false,
            StableSampleCount = 0,
            CaptureStabilityEvidence = "TEST_UNSTABLE"
        };
        var result = PublishGate.Evaluate(Input(contract, comparison: EquivalentComparison(contract)));
        Assert.False(result.Allowed);
        Assert.Contains("REQUEST_CAPTURE_STABILITY_NOT_ESTABLISHED", result.Reasons);
    }

    [Fact]
    public void MissingPublicationEvidenceBlocksNormalPublish()
    {
        var contract = Contract(PublicationPattern.DirectCreateAndWrite, publicationEvidence: false);
        var result = PublishGate.Evaluate(Input(contract, comparison: EquivalentComparison(contract)));
        Assert.False(result.Allowed);
        Assert.Contains("PUBLICATION_EVIDENCE_INCOMPLETE", result.Reasons);
    }

    [Fact]
    public void ComparisonIsMandatoryForNormalPublish()
    {
        var contract = Contract(PublicationPattern.DirectCreateAndWrite);
        var result = PublishGate.Evaluate(Input(contract, comparison: null));
        Assert.False(result.Allowed);
        Assert.Contains("PREPARED_REQUEST_COMPARISON_REQUIRED", result.Reasons);
    }

    [Fact]
    public void DifferentPreparedStructureBlocksBeforeNormalDispatch()
    {
        var contract = Contract(PublicationPattern.DirectCreateAndWrite);
        var prepared = Encoding.ASCII.GetBytes("Amount=200\ntype=1\r\nIP=1\r\nport=2\r\n");
        var comparison = RequestComparer.Compare(contract.ObservedRawBytes, prepared, new[] { "Amount" });
        Assert.Equal("DIFFERENT", comparison.Result);

        var result = PublishGate.Evaluate(Input(contract, comparison: comparison));
        Assert.False(result.Allowed);
        Assert.Contains("PREPARED_REQUEST_STRUCTURE_NOT_EQUIVALENT", result.Reasons);
    }

    [Fact]
    public void FullyQualifiedNormalEvidenceCanPassGateWithoutUnitConversion()
    {
        var contract = Contract(PublicationPattern.DirectCreateAndWrite);
        var comparison = EquivalentComparison(contract);
        var result = PublishGate.Evaluate(Input(contract, rawAmount: 321, comparison: comparison));
        Assert.True(result.Allowed);
        Assert.Equal("PUBLISH_ALLOWED_BY_OBSERVED_EVIDENCE", result.Status);
    }

    private static PublishGateInput Input(
        ObservedRequestContract contract,
        long rawAmount = 100,
        bool staleRequest = false,
        bool staleResponse = false,
        bool additionalUnitConfirmation = true,
        RequestComparison? comparison = null) => new(
            contract,
            rawAmount,
            100000,
            staleRequest,
            staleResponse,
            AmountRelationshipObserved: true,
            AmountUnitStillUnresolved: true,
            AdditionalUnresolvedUnitConfirmation: additionalUnitConfirmation,
            ExpertOverride: false,
            PreparedRequestComparison: comparison);

    private static RequestComparison EquivalentComparison(ObservedRequestContract contract)
    {
        var rendered = RequestTemplateRenderer.Render(contract, 200);
        return RequestComparer.Compare(contract.ObservedRawBytes, rendered.Bytes, rendered.ReplacedFields);
    }

    private static ObservedRequestContract Contract(PublicationPattern pattern, bool publicationEvidence = true)
    {
        var raw = Encoding.ASCII.GetBytes("Amount=100\r\ntype=1\r\nIP=1\r\nport=2\r\n");
        var now = DateTime.UtcNow;
        var samples = new[]
        {
            new ArtifactReadSample(1, DateTimeOffset.UtcNow, now, now, raw.Length, now, now, raw.Length, raw, Hashing.Sha256(raw), true),
            new ArtifactReadSample(2, DateTimeOffset.UtcNow, now, now, raw.Length, now, now, raw.Length, raw, Hashing.Sha256(raw), true)
        };
        var capture = new ArtifactCapture(
            "Request",
            Path.Combine(Path.GetTempPath(), "TransAction.txt"),
            "TransAction.txt",
            DateTimeOffset.UtcNow,
            now,
            now,
            raw,
            Hashing.Sha256(raw),
            CaptureStatus.ContentCaptured,
            CaptureStability.Stable,
            2,
            samples);
        return CandidateRequestAnalyzer.Analyze(capture) with
        {
            PublicationPattern = pattern,
            PublicationStrategyEvidence = "test",
            PublicationStrategyConfidence = "HIGH",
            ObservedTemporaryFileName = pattern == PublicationPattern.TempFileThenRename ? "tx.tmp" : null,
            PublicationEvidenceEstablished = publicationEvidence
        };
    }
}
