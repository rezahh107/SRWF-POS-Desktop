namespace SRWF.POS.PecProbe.Core;

public static class CandidateRequestAnalyzer
{
    private static readonly string[] CandidateOrder = ["Amount", "type", "IP", "port"];

    public static ObservedRequestContract Analyze(
        ArtifactCapture capture,
        PublicationPattern publicationPattern = PublicationPattern.Unknown,
        string strategyEvidence = "No publication pattern inference has been finalized.",
        string strategyConfidence = "LOW")
    {
        var textEvidence = EvidenceTextAnalyzer.Analyze(capture.RawBytes);
        var schema = ClassifySchema(textEvidence);
        var amountField = textEvidence.Fields.FirstOrDefault(kv => string.Equals(kv.Key, "Amount", StringComparison.OrdinalIgnoreCase));
        long? amount = long.TryParse(amountField.Value, out var parsed) ? parsed : null;
        var amountName = amountField.Key;

        return new(
            capture.FullPath,
            capture.FileName,
            textEvidence,
            schema,
            publicationPattern,
            strategyEvidence,
            strategyConfidence,
            null,
            string.IsNullOrWhiteSpace(amountName) ? null : amountName,
            amount,
            true,
            capture.RawBytes,
            capture.StabilityEstablished,
            capture.StabilityEstablished ? capture.SuccessfulSampleCount : 0,
            capture.StabilityEstablished
                ? $"At least two bounded shared-read samples matched with stable pre/post metadata; successfulSamples={capture.SuccessfulSampleCount}."
                : $"Capture stability not established; state={capture.Stability}; successfulSamples={capture.SuccessfulSampleCount}.",
            PublicationEvidenceEstablished: false);
    }

    public static CandidateSchemaMatch ClassifySchema(TextEvidence evidence)
    {
        if (!evidence.DecodingSafe || evidence.FieldNames.Count == 0)
            return CandidateSchemaMatch.InsufficientEvidence;

        if (evidence.FieldNames.SequenceEqual(CandidateOrder, StringComparer.Ordinal))
            return CandidateSchemaMatch.MatchesExternalCandidate;

        return CandidateSchemaMatch.DiffersFromExternalCandidate;
    }
}

public static class PublicationPatternInferer
{
    public static (PublicationPattern Pattern, string Evidence, string Confidence) Infer(
        IEnumerable<FilesystemEventRecord> events,
        string observedFinalPath,
        bool finalPathExistedBefore)
    {
        var normalized = Path.GetFullPath(observedFinalPath);
        var relevant = events.Where(e =>
            string.Equals(Path.GetFullPath(e.FullPath), normalized, StringComparison.OrdinalIgnoreCase) ||
            (e.OldFullPath is not null && string.Equals(Path.GetFullPath(e.OldFullPath), normalized, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.Sequence)
            .ToList();

        if (relevant.Any(e => e.EventType.Equals("Renamed", StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(Path.GetFullPath(e.FullPath), normalized, StringComparison.OrdinalIgnoreCase)))
            return (PublicationPattern.TempFileThenRename, "A rename event made the observed final path visible.", "HIGH");

        if (finalPathExistedBefore && relevant.Any(e => IsDeleteEvent(e.EventType)) &&
                                      relevant.Any(e => IsCreateEvent(e.EventType)))
            return (PublicationPattern.ReplaceExisting, "The final path existed before observation and delete/create activity was observed.", "MEDIUM");

        if (relevant.Any(e => IsCreateEvent(e.EventType)) &&
            !relevant.Any(e => e.EventType.Equals("Renamed", StringComparison.OrdinalIgnoreCase)))
            return (PublicationPattern.DirectCreateAndWrite, "Positive create visibility was observed directly on the final path without conflicting rename evidence.", "MEDIUM");

        if (relevant.Any(e => e.EventType.Contains("Changed", StringComparison.OrdinalIgnoreCase)))
            return (PublicationPattern.OtherObservedPattern, "Changed-only activity was observed on the path; this is insufficient to prove DIRECT_CREATE_AND_WRITE.", "LOW");

        if (events.Any())
            return (PublicationPattern.OtherObservedPattern, "Filesystem activity was observed but does not support a known publication pattern.", "LOW");

        return (PublicationPattern.Unknown, "No sufficient publication evidence was captured.", "LOW");
    }

    private static bool IsCreateEvent(string eventType) =>
        eventType.Equals("Created", StringComparison.OrdinalIgnoreCase) ||
        eventType.Equals("PollCreated", StringComparison.OrdinalIgnoreCase);

    private static bool IsDeleteEvent(string eventType) =>
        eventType.Equals("Deleted", StringComparison.OrdinalIgnoreCase) ||
        eventType.Equals("PollDeleted", StringComparison.OrdinalIgnoreCase);
}

public static class AmountRelationAnalyzer
{
    public static AmountAnalysis Analyze(OperatorObservationInput input, long? observedRequestAmount)
    {
        if (input.TesterEnteredAmount is null || observedRequestAmount is null)
            return new("AMOUNT_UNIT_NOT_PROVEN", input.TesterEnteredAmount, input.TesterDisplayedUnit, observedRequestAmount,
                AmountRelationship.Unknown, "Insufficient evidence to compare values.", "LOW");

        var tester = input.TesterEnteredAmount.Value;
        var request = observedRequestAmount.Value;
        AmountRelationship relation;
        if (request == tester) relation = AmountRelationship.Equal;
        else if (TryMultiplyBy10(tester, out var tester10) && request == tester10) relation = AmountRelationship.RequestEqualsTesterTimes10;
        else if (TryMultiplyBy10(request, out var request10) && request10 == tester) relation = AmountRelationship.RequestTimes10EqualsTester;
        else relation = AmountRelationship.Other;

        var interpretation = relation switch
        {
            AmountRelationship.Equal => "Observed Request amount equals the operator-reported Tester input.",
            AmountRelationship.RequestEqualsTesterTimes10 => "Observed Request amount is 10× the operator-reported Tester input.",
            AmountRelationship.RequestTimes10EqualsTester => "Observed Request amount is 1/10 of the operator-reported Tester input.",
            _ => "Observed values have another relationship."
        };

        return new("AMOUNT_RELATION_OBSERVED", tester, input.TesterDisplayedUnit, request, relation, interpretation,
            relation == AmountRelationship.Other ? "LOW" : "SINGLE_OBSERVATION");
    }

    private static bool TryMultiplyBy10(long value, out long result)
    {
        try { result = checked(value * 10); return true; }
        catch (OverflowException) { result = 0; return false; }
    }
}

public static class CandidateThirdTokenParser
{
    public static ResponseParseResult Parse(ReadOnlySpan<byte> rawBytes)
    {
        var evidence = EvidenceTextAnalyzer.Analyze(rawBytes);
        if (!evidence.DecodingSafe || evidence.Text is null)
            return new(CandidateResponseOutcome.ParseUnknown, null, "PARSE_UNKNOWN", null);

        var firstLine = evidence.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')[0];
        var tokens = firstLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3)
            return new(CandidateResponseOutcome.ParseUnknown, null, "PARSE_UNKNOWN", null);

        var code = tokens[2];
        return code switch
        {
            "00" => new(CandidateResponseOutcome.CandidateSuccess, code, "Candidate Success", 2),
            "99" => new(CandidateResponseOutcome.CandidateUserCancelled, code, "Candidate User Cancelled", 2),
            "51" => new(CandidateResponseOutcome.CandidateInsufficientFunds, code, "Candidate Insufficient Funds", 2),
            "55" => new(CandidateResponseOutcome.CandidateInvalidPin, code, "Candidate Invalid PIN", 2),
            _ => new(CandidateResponseOutcome.UnmappedResponse, code, "UNMAPPED_RESPONSE", 2)
        };
    }
}
