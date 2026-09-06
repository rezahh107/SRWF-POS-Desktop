namespace SRWF.POS.PecProbe.Core;

public static class PublishGate
{
    public static PublishGateResult Evaluate(PublishGateInput input)
    {
        var reasons = new List<string>();

        if (input.RawAmount <= 0) reasons.Add("RAW_AMOUNT_MUST_BE_POSITIVE");
        if (input.RawAmount > input.MaxProbeRawAmount) reasons.Add("RAW_AMOUNT_EXCEEDS_MAX_PROBE_RAW_AMOUNT");
        if (input.StaleRequestExists) reasons.Add("RECOVERY_REQUIRED_STALE_REQUEST");
        if (input.StaleResponseExists) reasons.Add("RECOVERY_REQUIRED_STALE_RESPONSE");
        if (input.UnresolvedPriorAttempt) reasons.Add("RECOVERY_REQUIRED_UNRESOLVED_PRIOR_ATTEMPT");

        if (!input.ExpertOverride)
        {
            if (input.Contract is null || !input.Contract.IsObservedTesterEvidence)
                reasons.Add("NO_OBSERVED_TESTER_REQUEST");
            else
            {
                if (!input.Contract.CaptureStabilityEstablished)
                    reasons.Add("REQUEST_CAPTURE_STABILITY_NOT_ESTABLISHED");
                if (!input.Contract.PublicationEvidenceEstablished)
                    reasons.Add("PUBLICATION_EVIDENCE_INCOMPLETE");
                if (!input.Contract.HasReproducibleStructure)
                    reasons.Add("PUBLISH_CONTRACT_INCOMPLETE");
                if (input.Contract.PublicationPattern is not PublicationPattern.DirectCreateAndWrite and not PublicationPattern.TempFileThenRename)
                    reasons.Add("PUBLICATION_STRATEGY_NOT_REPRODUCIBLE");
            }

            if (!input.AmountRelationshipObserved)
                reasons.Add("AMOUNT_RELATIONSHIP_NOT_OBSERVED");

            if (input.PreparedRequestComparison is null)
                reasons.Add("PREPARED_REQUEST_COMPARISON_REQUIRED");
            else if (input.PreparedRequestComparison.Result is not "BYTE_IDENTICAL" and not "STRUCTURALLY_EQUIVALENT")
                reasons.Add("PREPARED_REQUEST_STRUCTURE_NOT_EQUIVALENT");
        }
        else if (input.ExpertOverrideStrategy is null)
        {
            reasons.Add("UNVERIFIED_OVERRIDE_STRATEGY_REQUIRED");
        }

        if (input.AmountUnitStillUnresolved && !input.AdditionalUnresolvedUnitConfirmation)
            reasons.Add("UNRESOLVED_AMOUNT_UNIT_CONFIRMATION_REQUIRED");

        return reasons.Count == 0
            ? new(true, input.ExpertOverride ? "UNVERIFIED_PUBLISH_OVERRIDE" : "PUBLISH_ALLOWED_BY_OBSERVED_EVIDENCE", [])
            : new(false, reasons.Any(r => r.StartsWith("RECOVERY_REQUIRED", StringComparison.Ordinal)) ? "RECOVERY_REQUIRED" : "PUBLISH_BLOCKED", reasons);
    }
}

public static class RequestTemplateRenderer
{
    public static RequestRenderResult Render(
        ObservedRequestContract contract,
        long rawAmount,
        IReadOnlyDictionary<string, string>? supportedOverrides = null)
    {
        if (!contract.TextEvidence.DecodingSafe || contract.TextEvidence.Text is null)
            throw new InvalidOperationException("Observed Request encoding is not safely reproducible.");
        if (string.IsNullOrWhiteSpace(contract.AmountFieldName))
            throw new InvalidOperationException("Observed Request does not expose an identifiable amount field.");

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [contract.AmountFieldName] = rawAmount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        if (supportedOverrides is not null)
        {
            var observedNames = contract.TextEvidence.FieldNames.ToHashSet(StringComparer.Ordinal);
            foreach (var pair in supportedOverrides)
                if (observedNames.Contains(pair.Key)) replacements[pair.Key] = pair.Value;
        }

        var replaced = new HashSet<string>(StringComparer.Ordinal);
        var output = new System.Text.StringBuilder(contract.TextEvidence.Text.Length + 32);
        foreach (var segment in SplitPreservingDelimiters(contract.TextEvidence.Text))
        {
            var content = segment.Content;
            var equals = content.IndexOf('=');
            if (equals > 0)
            {
                var key = content[..equals];
                if (replacements.TryGetValue(key, out var value))
                {
                    content = key + "=" + value;
                    replaced.Add(key);
                }
            }
            output.Append(content);
            output.Append(segment.Delimiter);
        }

        if (!replaced.Contains(contract.AmountFieldName))
            throw new InvalidOperationException("Amount field could not be replaced without rewriting unknown structure.");

        var bytes = EvidenceTextAnalyzer.EncodeLike(contract.TextEvidence, output.ToString());
        return new(bytes, Hashing.Sha256(bytes), replaced.Order(StringComparer.Ordinal).ToArray());
    }

    internal static IReadOnlyList<(string Content, string Delimiter)> SplitPreservingDelimiters(string text)
    {
        var segments = new List<(string Content, string Delimiter)>();
        if (text.Length == 0)
        {
            segments.Add((string.Empty, string.Empty));
            return segments;
        }

        var start = 0;
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] is not ('\r' or '\n'))
            {
                index++;
                continue;
            }

            var delimiter = text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n'
                ? "\r\n"
                : text[index].ToString();
            segments.Add((text[start..index], delimiter));
            index += delimiter.Length;
            start = index;
        }

        if (start < text.Length)
            segments.Add((text[start..], string.Empty));
        return segments;
    }
}

public static class RequestComparer
{
    public static RequestComparison Compare(
        ReadOnlySpan<byte> observed,
        ReadOnlySpan<byte> prepared,
        IReadOnlyCollection<string>? allowedDynamicFields = null)
    {
        var o = EvidenceTextAnalyzer.Analyze(observed);
        var p = EvidenceTextAnalyzer.Analyze(prepared);
        var byteIdentical = observed.SequenceEqual(prepared);
        var encodingSame = o.ProbableEncoding == p.ProbableEncoding;
        var bomSame = o.BomPresent == p.BomPresent;
        var orderSame = o.FieldNames.SequenceEqual(p.FieldNames, StringComparer.Ordinal);
        var finalNewlineSame = o.FinalNewlinePresent == p.FinalNewlinePresent;

        if (byteIdentical)
        {
            return new("BYTE_IDENTICAL", encodingSame, bomSame, true, orderSame, finalNewlineSame, true,
                Hashing.Sha256(observed), Hashing.Sha256(prepared),
                ["Exact observed and prepared bytes match."]);
        }

        if (!o.DecodingSafe || !p.DecodingSafe || o.Text is null || p.Text is null)
        {
            return new("NOT_COMPARABLE", encodingSame, bomSame, false, orderSame, finalNewlineSame, false,
                Hashing.Sha256(observed), Hashing.Sha256(prepared),
                ["At least one Request cannot be decoded safely for structural comparison."]);
        }

        var dynamicFields = allowedDynamicFields is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : allowedDynamicFields.ToHashSet(StringComparer.Ordinal);
        var observedSegments = RequestTemplateRenderer.SplitPreservingDelimiters(o.Text);
        var preparedSegments = RequestTemplateRenderer.SplitPreservingDelimiters(p.Text);
        var delimiterSequenceSame = observedSegments.Count == preparedSegments.Count;
        var nonReplacedContentSame = observedSegments.Count == preparedSegments.Count;

        if (observedSegments.Count == preparedSegments.Count)
        {
            for (var i = 0; i < observedSegments.Count; i++)
            {
                var left = observedSegments[i];
                var right = preparedSegments[i];
                if (!string.Equals(left.Delimiter, right.Delimiter, StringComparison.Ordinal))
                    delimiterSequenceSame = false;
                if (string.Equals(left.Content, right.Content, StringComparison.Ordinal))
                    continue;

                if (!TryGetFieldKey(left.Content, out var leftKey) ||
                    !TryGetFieldKey(right.Content, out var rightKey) ||
                    !string.Equals(leftKey, rightKey, StringComparison.Ordinal) ||
                    !dynamicFields.Contains(leftKey))
                {
                    nonReplacedContentSame = false;
                }
            }
        }

        var structurallyEquivalent = encodingSame &&
                                     bomSame &&
                                     delimiterSequenceSame &&
                                     orderSame &&
                                     finalNewlineSame &&
                                     nonReplacedContentSame;
        var result = structurallyEquivalent ? "STRUCTURALLY_EQUIVALENT" : "DIFFERENT";
        return new(result, encodingSame, bomSame, delimiterSequenceSame, orderSame, finalNewlineSame, false,
            Hashing.Sha256(observed), Hashing.Sha256(prepared),
            [
                "Structural equivalence requires exact delimiter sequence and exact non-replaced content.",
                $"nonReplacedContentSame={nonReplacedContentSame}",
                "Dynamic transactions are never labelled byte-identical unless the exact bytes match."
            ]);
    }

    private static bool TryGetFieldKey(string line, out string key)
    {
        var equals = line.IndexOf('=');
        if (equals <= 0)
        {
            key = string.Empty;
            return false;
        }
        key = line[..equals];
        return true;
    }
}
