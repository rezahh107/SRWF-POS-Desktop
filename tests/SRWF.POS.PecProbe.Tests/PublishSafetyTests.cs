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
        var contract = Contract(PublicationPattern.Unknown);
        var result = PublishGate.Evaluate(new(contract, 100, 100000, false, false, true, true, true, false));
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
    public void RawAmountOverHardCeilingIsRejected()
    {
        var result = PublishGate.Evaluate(new(Contract(PublicationPattern.DirectCreateAndWrite), 100001, 100000, false, false, true, true, true, false));
        Assert.False(result.Allowed);
        Assert.Contains("RAW_AMOUNT_EXCEEDS_MAX_PROBE_RAW_AMOUNT", result.Reasons);
    }

    [Fact]
    public void StaleRequestBlocksDispatch()
    {
        var result = PublishGate.Evaluate(new(Contract(PublicationPattern.DirectCreateAndWrite), 100, 100000, true, false, true, true, true, false));
        Assert.False(result.Allowed);
        Assert.Equal("RECOVERY_REQUIRED", result.Status);
    }

    [Fact]
    public void StaleResponseBlocksDispatch()
    {
        var result = PublishGate.Evaluate(new(Contract(PublicationPattern.DirectCreateAndWrite), 100, 100000, false, true, true, true, true, false));
        Assert.False(result.Allowed);
        Assert.Equal("RECOVERY_REQUIRED", result.Status);
    }

    [Fact]
    public void UnresolvedUnitNeedsAdditionalConfirmation()
    {
        var result = PublishGate.Evaluate(new(Contract(PublicationPattern.DirectCreateAndWrite), 100, 100000, false, false, true, true, false, false));
        Assert.False(result.Allowed);
        Assert.Contains("UNRESOLVED_AMOUNT_UNIT_CONFIRMATION_REQUIRED", result.Reasons);
    }

    [Fact]
    public void TempRenameWithoutObservedTempNameIsIncomplete()
    {
        var contract = Contract(PublicationPattern.TempFileThenRename) with { ObservedTemporaryFileName = null };
        Assert.False(contract.HasReproducibleStructure);
    }

    private static ObservedRequestContract Contract(PublicationPattern pattern)
    {
        var raw = Encoding.ASCII.GetBytes("Amount=100\r\ntype=1\r\nIP=1\r\nport=2\r\n");
        var capture = new ArtifactCapture("Request", Path.Combine(Path.GetTempPath(), "TransAction.txt"), "TransAction.txt", DateTimeOffset.UtcNow,
            DateTime.UtcNow, DateTime.UtcNow, raw, Hashing.Sha256(raw));
        return CandidateRequestAnalyzer.Analyze(capture) with
        {
            PublicationPattern = pattern,
            PublicationStrategyEvidence = "test",
            PublicationStrategyConfidence = "HIGH",
            ObservedTemporaryFileName = pattern == PublicationPattern.TempFileThenRename ? "tx.tmp" : null
        };
    }
}
