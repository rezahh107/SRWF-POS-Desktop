using System.Text;
using SRWF.POS.PecProbe.Core;

namespace SRWF.POS.PecProbe.Tests;

public class ResponseAndSanitizationTests
{
    [Theory]
    [InlineData("00", CandidateResponseOutcome.CandidateSuccess, "Candidate Success")]
    [InlineData("99", CandidateResponseOutcome.CandidateUserCancelled, "Candidate User Cancelled")]
    [InlineData("51", CandidateResponseOutcome.CandidateInsufficientFunds, "Candidate Insufficient Funds")]
    [InlineData("55", CandidateResponseOutcome.CandidateInvalidPin, "Candidate Invalid PIN")]
    [InlineData("77", CandidateResponseOutcome.UnmappedResponse, "UNMAPPED_RESPONSE")]
    public void CandidateThirdTokenMappingsStayCandidateOnly(string code, CandidateResponseOutcome expected, string label)
    {
        var result = CandidateThirdTokenParser.Parse(Encoding.ASCII.GetBytes($"RESULT CODE {code}\r\n"));
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(label, result.Label);
        Assert.Equal("CANDIDATE_MAPPING_ONLY", result.EvidenceLevel);
    }

    [Fact]
    public void MissingThirdTokenIsParseUnknown()
    {
        var result = CandidateThirdTokenParser.Parse(Encoding.ASCII.GetBytes("RESULT 00\r\n"));
        Assert.Equal(CandidateResponseOutcome.ParseUnknown, result.Outcome);
        Assert.Equal("PARSE_UNKNOWN", result.Label);
    }

    [Fact]
    public void LuhnValidPanIsRedactedWithMetadata()
    {
        const string pan = "4111111111111111";
        var result = SensitiveDataSanitizer.Sanitize("code 00 pan " + pan + " trace 12345678");
        Assert.DoesNotContain(pan, result.SanitizedText);
        var finding = Assert.Single(result.Findings.Where(f => f.RedactionReason == "LUHN_VALID_PAN_CANDIDATE"));
        Assert.Equal(16, finding.OriginalLength);
        Assert.Equal(64, finding.StableHash.Length);
        Assert.True(finding.TokenIndex >= 0);
        Assert.Contains("12345678", result.SanitizedText);
    }

    [Fact]
    public void Track2LikeDataIsFullyRedacted()
    {
        const string track2 = "4111111111111111=29121234567890000000";
        var result = SensitiveDataSanitizer.Sanitize("x " + track2 + " y");
        Assert.DoesNotContain(track2, result.SanitizedText);
        Assert.Contains(result.Findings, f => f.RedactionReason == "TRACK2_LIKE");
    }

    [Fact]
    public void NonSensitiveReferenceNumberRemainsUseful()
    {
        const string reference = "123456789012";
        var result = SensitiveDataSanitizer.Sanitize("ref " + reference);
        Assert.Contains(reference, result.SanitizedText);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void OperationalTimeoutIsUnknownAndNeverAutoRetries()
    {
        Assert.Equal("UNKNOWN", TimeoutSemantics.OperationalTimeoutOutcome);
        Assert.False(TimeoutSemantics.AutomaticRetryAllowed);
    }
}
