using System.Text.Json;

namespace SRWF.POS.PecProbe.Core;

public static class SessionRecoveryClassifier
{
    private static readonly HashSet<string> DispatchMarkers = new(StringComparer.Ordinal)
    {
        "DISPATCH_STARTED",
        "REQUEST_FILE_PUBLISHED"
    };

    private static readonly JsonSerializerOptions TimelineJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static RecoveryAssessment Assess(string diagnosticsRoot)
    {
        if (!Directory.Exists(diagnosticsRoot))
            return new(false, "NO_PRIOR_PUBLISH_EVIDENCE", []);

        var findings = new List<RecoveryFinding>();
        IEnumerable<string> sessions;
        try
        {
            sessions = Directory.EnumerateDirectories(diagnosticsRoot).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            findings.Add(new(diagnosticsRoot, "DIAGNOSTICS_ROOT", "UNREADABLE_RELEVANT_EVIDENCE", ex.Message));
            return new(true, "RECOVERY_REQUIRED", findings);
        }

        foreach (var sessionDirectory in sessions)
        {
            var publishAttempt = Path.Combine(sessionDirectory, "publish-attempt.json");
            if (File.Exists(publishAttempt))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(publishAttempt));
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        throw new JsonException("publish-attempt.json root is not an object.");
                    findings.Add(new(sessionDirectory, "publish-attempt.json", "UNRESOLVED_DURABLE_PUBLISH_ATTEMPT"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    findings.Add(new(sessionDirectory, "publish-attempt.json", "UNREADABLE_OR_CORRUPT_PUBLISH_EVIDENCE", ex.Message));
                }
                continue;
            }

            var timeline = Path.Combine(sessionDirectory, "timeline.jsonl");
            if (!File.Exists(timeline))
                continue;

            try
            {
                foreach (var line in File.ReadLines(timeline))
                {
                    if (!TryGetDispatchMarker(line, out var marker))
                        continue;

                    findings.Add(new(sessionDirectory, "timeline.jsonl", "UNRESOLVED_DISPATCH_MARKER", marker));
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A timeline alone is not a publish-attempt authority. Without a durable publish-attempt
                // marker we do not fabricate a prior dispatch solely because an observation timeline is unreadable.
            }
        }

        return findings.Count == 0
            ? new(false, "NO_PRIOR_PUBLISH_EVIDENCE", [])
            : new(true, "RECOVERY_REQUIRED", findings);
    }

    private static bool TryGetDispatchMarker(string line, out string? marker)
    {
        marker = null;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        TimelineEventIdentity? record;
        try
        {
            record = JsonSerializer.Deserialize<TimelineEventIdentity>(line, TimelineJsonOptions);
        }
        catch (JsonException)
        {
            // timeline.jsonl is compatibility/fallback evidence, not the primary durable publish authority.
            // Preserve malformed evidence on disk and do not infer a dispatch from free text inside invalid JSON.
            return false;
        }

        if (record is null ||
            !string.Equals(record.DirectoryRole, "Publish", StringComparison.Ordinal) ||
            record.EvidenceStatus is null ||
            !DispatchMarkers.Contains(record.EvidenceStatus))
            return false;

        marker = record.EvidenceStatus;
        return true;
    }

    private sealed record TimelineEventIdentity(string? DirectoryRole, string? EvidenceStatus);
}
