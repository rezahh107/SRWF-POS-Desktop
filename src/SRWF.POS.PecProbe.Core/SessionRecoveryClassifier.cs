using System.Text.Json;

namespace SRWF.POS.PecProbe.Core;

public static class SessionRecoveryClassifier
{
    private static readonly string[] DispatchMarkers = ["DISPATCH_STARTED", "REQUEST_FILE_PUBLISHED"];

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
                var marker = File.ReadLines(timeline)
                    .SelectMany(line => DispatchMarkers.Where(marker => line.Contains(marker, StringComparison.Ordinal)))
                    .FirstOrDefault();
                if (marker is not null)
                    findings.Add(new(sessionDirectory, "timeline.jsonl", "UNRESOLVED_DISPATCH_MARKER", marker));
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
}
