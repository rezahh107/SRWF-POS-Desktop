using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace SRWF.POS.PecProbe.Core;

public sealed class DiagnosticCollector
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private long _sequence;
    private readonly List<FilesystemEventRecord> _events = [];
    private readonly List<string> _errors = [];
    private string? _promotedRequestHash;
    private bool _requestConflictObserved;

    public string SessionDirectory { get; }
    public DiagnosticSessionMetadata Session { get; private set; }
    public ArtifactCapture? LatestRequestCapture { get; private set; }
    public ArtifactCapture? LatestResponseCapture { get; private set; }
    public ObservedRequestContract? RequestContract { get; private set; }
    public AmountAnalysis? AmountAnalysis { get; private set; }
    public ResponseParseResult? ResponseParsing { get; private set; }

    public IReadOnlyList<FilesystemEventRecord> Events { get { lock (_gate) return _events.ToArray(); } }

    public DiagnosticCollector(ProbeConfiguration configuration, DiagnosticSessionMetadata session)
    {
        EnsureDiagnosticsOutsideProviderDirectories(configuration);
        Session = session;
        SessionDirectory = Path.Combine(configuration.DiagnosticsRoot, session.SessionId);
        Directory.CreateDirectory(SessionDirectory);
        Directory.CreateDirectory(Path.Combine(SessionDirectory, "request-candidates"));
        Directory.CreateDirectory(Path.Combine(SessionDirectory, "response-candidates"));
        Directory.CreateDirectory(Path.Combine(SessionDirectory, "provider-archive"));
        WriteJson("session.json", Session);
        WriteJson("configuration.json", configuration);
        File.WriteAllText(Path.Combine(SessionDirectory, "filesystem-events.jsonl"), string.Empty, Encoding.UTF8);
        File.WriteAllText(Path.Combine(SessionDirectory, "timeline.jsonl"), string.Empty, Encoding.UTF8);
        File.WriteAllText(Path.Combine(SessionDirectory, "errors.jsonl"), string.Empty, Encoding.UTF8);
    }

    public void WriteEnvironment(object environment) => WriteJson("environment.json", environment);
    public void WriteServices(object services) => WriteJson("pec-services.json", services);
    public void WriteDirectoryBefore(object snapshots) => WriteJson("directory-before.json", snapshots);
    public void WriteDirectoryAfter(object snapshots) => WriteJson("directory-after.json", snapshots);

    public FilesystemEventRecord RecordEvent(string role, string eventType, string fullPath, string? oldPath = null, long? length = null, string? sha = null, string status = "EVENT_OBSERVED", string? note = null)
    {
        FilesystemEventRecord record;
        lock (_gate)
        {
            record = new(++_sequence, DateTimeOffset.UtcNow, role, eventType, fullPath, Path.GetFileName(fullPath), oldPath, length, sha, status, note);
            _events.Add(record);
            AppendJsonLine("filesystem-events.jsonl", record);
            AppendJsonLine("timeline.jsonl", record);
        }
        return record;
    }

    public void RecordError(string code, Exception ex)
    {
        lock (_gate)
        {
            _errors.Add(code + ": " + ex.Message);
            AppendJsonLine("errors.jsonl", new { atUtc = DateTimeOffset.UtcNow, code, type = ex.GetType().Name, message = ex.Message });
        }
    }

    public void RecordRequestCapture(ArtifactCapture capture, OperatorObservationInput input)
    {
        lock (_gate)
        {
            LatestRequestCapture = capture;
            PersistRequestSamples(capture);
            WriteJson("request-capture-stability.json", new
            {
                capture.FullPath,
                capture.FileName,
                capture.CapturedAtUtc,
                capture.Stability,
                capture.SuccessfulSampleCount,
                stabilityEstablished = capture.StabilityEstablished,
                sampleHashes = (capture.Samples ?? []).Select(s => new
                {
                    s.SampleNumber,
                    s.SampledAtUtc,
                    s.LengthBefore,
                    s.LengthAfter,
                    s.CreationTimeUtcBefore,
                    s.CreationTimeUtcAfter,
                    s.LastWriteTimeUtcBefore,
                    s.LastWriteTimeUtcAfter,
                    s.Sha256,
                    s.MetadataStableAcrossRead
                }).ToArray()
            });

            if (capture.StabilityEstablished)
            {
                if (RequestContract is null && !_requestConflictObserved)
                {
                    PromoteStableRequest(capture, input);
                    return;
                }

                if (RequestContract is not null &&
                    string.Equals(_promotedRequestHash, capture.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    RequestContract = RequestContract with
                    {
                        StableSampleCount = Math.Max(RequestContract.StableSampleCount, capture.SuccessfulSampleCount),
                        CaptureStabilityEvidence = $"Stable repeated capture reconfirmed; successfulSamples={capture.SuccessfulSampleCount}."
                    };
                    WriteContractFiles();
                    return;
                }

                if (RequestContract is not null)
                {
                    InvalidateRequestContract("CONFLICTING_STABLE_CAPTURE_OBSERVED");
                    return;
                }
            }

            if (RequestContract is not null && CaptureConflictsWithPromotedRequest(capture))
                InvalidateRequestContract("LATER_CONFLICTING_SAMPLE_INVALIDATED_STABILITY");

            WriteObservedRequestMetadata(capture, promoted: false);
        }
    }

    public void RecordRequestMiss(string path) => RecordEvent("Request", "Capture", path, status: "REQUEST_CONTENT_NOT_CAPTURED", note: "Request activity was observed but exact content could not be captured before disappearance/lock boundary.");

    public void FinalizePublicationPattern(bool finalPathExistedBefore)
    {
        lock (_gate)
        {
            if (RequestContract is null) return;
            if (!RequestContract.CaptureStabilityEstablished || _requestConflictObserved)
            {
                RequestContract = RequestContract with
                {
                    PublicationPattern = PublicationPattern.Unknown,
                    PublicationEvidenceEstablished = false,
                    PublicationStrategyEvidence = "Publication cannot be promoted because stable Request capture evidence was not retained through observation finalization.",
                    PublicationStrategyConfidence = "INCOMPLETE"
                };
                WriteContractFiles();
                return;
            }

            var currentPath = Path.GetFullPath(RequestContract.DestinationPath);
            var renames = _events
                .Where(e => e.EventType.Equals("Renamed", StringComparison.OrdinalIgnoreCase) && e.OldFullPath is not null)
                .OrderBy(e => e.Sequence)
                .ToArray();

            var renameFromCurrent = renames.FirstOrDefault(e =>
                string.Equals(Path.GetFullPath(e.OldFullPath!), currentPath, StringComparison.OrdinalIgnoreCase));
            if (renameFromCurrent is not null)
            {
                var finalPath = Path.GetFullPath(renameFromCurrent.FullPath);
                var finalStableCapture = FindStableCaptureEvent(finalPath, _promotedRequestHash);
                var finalBytesEquivalent = finalStableCapture is not null;
                RequestContract = RequestContract with
                {
                    DestinationPath = renameFromCurrent.FullPath,
                    FileName = Path.GetFileName(renameFromCurrent.FullPath),
                    PublicationPattern = PublicationPattern.TempFileThenRename,
                    PublicationStrategyEvidence = finalBytesEquivalent
                        ? "Observed temporary filename was renamed to the final path and stable final-path bytes matched the promoted Request SHA-256."
                        : "Observed temporary filename was renamed, but stable byte-equivalent final-path capture was not established.",
                    PublicationStrategyConfidence = finalBytesEquivalent ? "HIGH" : "INCOMPLETE",
                    ObservedTemporaryFileName = Path.GetFileName(renameFromCurrent.OldFullPath!),
                    PublicationEvidenceEstablished = finalBytesEquivalent
                };
                WriteContractFiles();
                return;
            }

            var renameToCurrent = renames.FirstOrDefault(e =>
                string.Equals(Path.GetFullPath(e.FullPath), currentPath, StringComparison.OrdinalIgnoreCase));
            if (renameToCurrent is not null)
            {
                RequestContract = RequestContract with
                {
                    PublicationPattern = PublicationPattern.TempFileThenRename,
                    PublicationStrategyEvidence = "Observed temporary filename was renamed to the stable captured final Request path.",
                    PublicationStrategyConfidence = "HIGH",
                    ObservedTemporaryFileName = Path.GetFileName(renameToCurrent.OldFullPath!),
                    PublicationEvidenceEstablished = true
                };
                WriteContractFiles();
                return;
            }

            var sameHashOnDifferentPath = _events.Any(e =>
                e.DirectoryRole == "Request" &&
                e.EvidenceStatus == "REQUEST_CONTENT_CAPTURED_STABLE" &&
                !string.Equals(Path.GetFullPath(e.FullPath), currentPath, StringComparison.OrdinalIgnoreCase) &&
                _promotedRequestHash is not null &&
                string.Equals(e.Sha256, _promotedRequestHash, StringComparison.OrdinalIgnoreCase));
            if (sameHashOnDifferentPath)
            {
                RequestContract = RequestContract with
                {
                    PublicationPattern = PublicationPattern.OtherObservedPattern,
                    PublicationStrategyEvidence = "Matching stable Request bytes appeared at multiple paths without a captured rename; publication method remains ambiguous.",
                    PublicationStrategyConfidence = "INCOMPLETE",
                    PublicationEvidenceEstablished = false
                };
                WriteContractFiles();
                return;
            }

            var inference = PublicationPatternInferer.Infer(_events, RequestContract.DestinationPath, finalPathExistedBefore);
            var directCreateProven = inference.Pattern == PublicationPattern.DirectCreateAndWrite &&
                                     FindStableCaptureEvent(currentPath, _promotedRequestHash) is not null;
            RequestContract = RequestContract with
            {
                PublicationPattern = inference.Pattern,
                PublicationStrategyEvidence = directCreateProven
                    ? inference.Evidence + " Stable final-path bytes were captured with the promoted SHA-256."
                    : inference.Evidence,
                PublicationStrategyConfidence = directCreateProven ? "HIGH" : inference.Confidence,
                ObservedTemporaryFileName = null,
                PublicationEvidenceEstablished = directCreateProven
            };
            WriteContractFiles();
        }
    }

    private FilesystemEventRecord? FindStableCaptureEvent(string normalizedPath, string? sha) =>
        _events
            .Where(e => e.DirectoryRole == "Request" && e.EvidenceStatus == "REQUEST_CONTENT_CAPTURED_STABLE")
            .OrderByDescending(e => e.Sequence)
            .FirstOrDefault(e =>
                string.Equals(Path.GetFullPath(e.FullPath), normalizedPath, StringComparison.OrdinalIgnoreCase) &&
                sha is not null &&
                string.Equals(e.Sha256, sha, StringComparison.OrdinalIgnoreCase));

    private void PromoteStableRequest(ArtifactCapture capture, OperatorObservationInput input)
    {
        _promotedRequestHash = capture.Sha256;
        File.WriteAllBytes(Path.Combine(SessionDirectory, "observed-request.raw"), capture.RawBytes);
        WriteObservedRequestMetadata(capture, promoted: true);
        RequestContract = CandidateRequestAnalyzer.Analyze(capture);
        AmountAnalysis = AmountRelationAnalyzer.Analyze(input, RequestContract.ObservedRawAmount);
        WriteContractFiles();
    }

    private void PersistRequestSamples(ArtifactCapture capture)
    {
        var seq = Interlocked.Read(ref _sequence);
        var samples = capture.Samples;
        if (samples is null || samples.Count == 0)
        {
            File.WriteAllBytes(Path.Combine(SessionDirectory, "request-candidates", $"{seq:D8}-{SafeName(capture.FileName)}.raw"), capture.RawBytes);
            return;
        }

        foreach (var sample in samples)
            File.WriteAllBytes(
                Path.Combine(SessionDirectory, "request-candidates", $"{seq:D8}-s{sample.SampleNumber:D2}-{SafeName(capture.FileName)}.raw"),
                sample.RawBytes);
    }

    private bool CaptureConflictsWithPromotedRequest(ArtifactCapture capture)
    {
        if (_promotedRequestHash is null) return false;
        var samples = capture.Samples;
        if (samples is null || samples.Count == 0)
            return !string.Equals(capture.Sha256, _promotedRequestHash, StringComparison.OrdinalIgnoreCase);
        return samples.Any(sample => !string.Equals(sample.Sha256, _promotedRequestHash, StringComparison.OrdinalIgnoreCase));
    }

    private void InvalidateRequestContract(string reason)
    {
        if (RequestContract is null) return;
        _requestConflictObserved = true;
        RequestContract = RequestContract with
        {
            CaptureStabilityEstablished = false,
            CaptureStabilityEvidence = reason,
            PublicationEvidenceEstablished = false,
            PublicationStrategyConfidence = "INCOMPLETE"
        };
        RecordEvent("Request", "CaptureStability", RequestContract.DestinationPath,
            status: "REQUEST_CONTENT_UNSTABLE", note: reason);
        WriteContractFiles();
    }

    private void WriteObservedRequestMetadata(ArtifactCapture capture, bool promoted)
    {
        var evidence = EvidenceTextAnalyzer.Analyze(capture.RawBytes);
        WriteJson("observed-request-metadata.json", new
        {
            status = promoted ? "REQUEST_CONTENT_CAPTURED_STABLE" : "REQUEST_CONTENT_CAPTURED_NOT_PROMOTED",
            capture.FileName,
            capture.FullPath,
            byteLength = capture.RawBytes.Length,
            capture.Sha256,
            capture.CreationTimeUtc,
            capture.LastWriteTimeUtc,
            capture.CapturedAtUtc,
            capture.Stability,
            capture.SuccessfulSampleCount,
            stabilityEstablished = capture.StabilityEstablished,
            evidence.ProbableEncoding,
            evidence.BomPresent,
            evidence.NewlineRepresentation,
            evidence.FinalNewlinePresent,
            evidence.DecodingSafe,
            fieldOrder = evidence.FieldNames
        });
    }

    private void WriteContractFiles()
    {
        if (RequestContract is null) return;
        WriteJson("request-contract-analysis.json", new
        {
            evidenceClass = "DERIVED_CANDIDATE_CONTRACT",
            sourceEvidenceClass = "OBSERVED_TESTER_EVIDENCE",
            RequestContract.FileName,
            RequestContract.DestinationPath,
            encoding = RequestContract.TextEvidence.ProbableEncoding,
            bom = RequestContract.TextEvidence.BomPresent,
            newline = RequestContract.TextEvidence.NewlineRepresentation,
            finalNewline = RequestContract.TextEvidence.FinalNewlinePresent,
            fieldOrder = RequestContract.TextEvidence.FieldNames,
            candidateSchema = RequestContract.CandidateSchemaMatch.ToString(),
            publicationPattern = RequestContract.PublicationPattern.ToString(),
            strategyEvidence = RequestContract.PublicationStrategyEvidence,
            strategyConfidence = RequestContract.PublicationStrategyConfidence,
            publicationEvidenceEstablished = RequestContract.PublicationEvidenceEstablished,
            captureStabilityEstablished = RequestContract.CaptureStabilityEstablished,
            stableSampleCount = RequestContract.StableSampleCount,
            captureStabilityEvidence = RequestContract.CaptureStabilityEvidence,
            observedTemporaryFileName = RequestContract.ObservedTemporaryFileName,
            amountFieldName = RequestContract.AmountFieldName,
            observedRawAmount = RequestContract.ObservedRawAmount,
            normalPublishReproducible = RequestContract.HasReproducibleStructure,
            officialPecContract = false
        });
        if (AmountAnalysis is not null) WriteJson("amount-analysis.json", AmountAnalysis);
    }

    public void RecordResponseCapture(ArtifactCapture capture)
    {
        lock (_gate)
        {
            LatestResponseCapture = capture;
            var seq = Interlocked.Read(ref _sequence);
            File.WriteAllBytes(Path.Combine(SessionDirectory, "response-candidates", $"{seq:D8}-{SafeName(capture.FileName)}.raw"), capture.RawBytes);
            File.WriteAllBytes(Path.Combine(SessionDirectory, "observed-response.raw"), capture.RawBytes);
            var evidence = EvidenceTextAnalyzer.Analyze(capture.RawBytes);
            WriteJson("response-metadata.json", new
            {
                status = "RESPONSE_CONTENT_CAPTURED",
                capture.FileName,
                capture.FullPath,
                byteLength = capture.RawBytes.Length,
                capture.Sha256,
                capture.CreationTimeUtc,
                capture.LastWriteTimeUtc,
                capture.CapturedAtUtc,
                capture.Stability,
                capture.SuccessfulSampleCount,
                evidence.ProbableEncoding,
                evidence.BomPresent,
                evidence.NewlineRepresentation,
                evidence.FinalNewlinePresent,
                evidence.DecodingSafe
            });
            ResponseParsing = CandidateThirdTokenParser.Parse(capture.RawBytes);
            WriteJson("response-parsing.json", ResponseParsing);
        }
    }

    public void RecordPublishedRequest(byte[] bytes, PublicationStrategy strategy, string strategyEvidence, string confidence, long rawAmount, RequestComparison comparison)
    {
        lock (_gate)
        {
            File.WriteAllBytes(Path.Combine(SessionDirectory, "published-request.raw"), bytes);
            WriteJson("published-request-metadata.json", new
            {
                persistedBeforeDispatch = true,
                byteLength = bytes.Length,
                sha256 = Hashing.Sha256(bytes),
                rawAmount,
                publicationStrategy = strategy.ToString(),
                strategyEvidence,
                strategyConfidence = confidence,
                createdAtUtc = DateTimeOffset.UtcNow
            });
            WriteJson("request-comparison.json", comparison);
        }
    }

    public void RecordPublishOverride(PublicationStrategy strategy) =>
        RecordEvent("Publish", "Override", SessionDirectory, status: "UNVERIFIED_PUBLISH_OVERRIDE", note: $"Explicit expert override selected strategy {strategy}.");

    public void Complete(string status)
    {
        lock (_gate)
        {
            Session = Session with { Status = status };
            WriteJson("session.json", Session);
            WriteSummary(status);
            WriteEvidenceInventory();
            WriteHashes();
        }
    }

    public void WriteJson(string fileName, object value)
    {
        File.WriteAllText(Path.Combine(SessionDirectory, fileName), JsonSerializer.Serialize(value, _json), Encoding.UTF8);
    }

    private void AppendJsonLine(string fileName, object value)
    {
        File.AppendAllText(Path.Combine(SessionDirectory, fileName), JsonSerializer.Serialize(value) + Environment.NewLine, Encoding.UTF8);
    }

    private void WriteEvidenceInventory()
    {
        var expected = new[]
        {
            "session.json","environment.json","pec-services.json","configuration.json","directory-before.json","directory-after.json",
            "filesystem-events.jsonl","timeline.jsonl","observed-request.raw","observed-request-metadata.json","request-capture-stability.json","request-contract-analysis.json",
            "published-request.raw","published-request-metadata.json","request-comparison.json","observed-response.raw","response-metadata.json",
            "response-parsing.json","amount-analysis.json","errors.jsonl","SUMMARY.md","hashes.sha256"
        };
        WriteJson("evidence-inventory.json", expected.Select(name => new { name, status = File.Exists(Path.Combine(SessionDirectory, name)) ? "PRESENT" : "MISSING_EVIDENCE" }).ToArray());
    }

    private void WriteSummary(string status)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SRWF-POS-PEC-Probe Diagnostic Summary");
        sb.AppendLine();
        sb.AppendLine($"- Session: `{Session.SessionId}`");
        sb.AppendLine($"- Status: `{status}`");
        sb.AppendLine("- PEC_ADAPTER_STATUS: `IMPLEMENTED_AGAINST_EXTERNAL_EVIDENCE`");
        sb.AppendLine("- PRODUCTION_VALIDATION: `PENDING_REAL_PEC_VALIDATION`");
        sb.AppendLine("- Official PEC contract claimed: `NO`");
        sb.AppendLine($"- Request evidence: `{(RequestContract?.CaptureStabilityEstablished == true ? "STABLE_CAPTURE" : LatestRequestCapture is null ? "MISSING_EVIDENCE" : "UNSTABLE_OR_UNCONFIRMED")}`");
        sb.AppendLine($"- Response evidence: `{(LatestResponseCapture is null ? "MISSING_EVIDENCE" : "CAPTURED")}`");
        sb.AppendLine($"- Publication pattern: `{RequestContract?.PublicationPattern.ToString() ?? "UNKNOWN"}`");
        sb.AppendLine($"- Publication evidence established: `{RequestContract?.PublicationEvidenceEstablished.ToString() ?? "False"}`");
        sb.AppendLine($"- Amount proof: `{AmountAnalysis?.ProofStatus ?? "AMOUNT_UNIT_NOT_PROVEN"}`");
        File.WriteAllText(Path.Combine(SessionDirectory, "SUMMARY.md"), sb.ToString(), Encoding.UTF8);
    }

    private void WriteHashes()
    {
        var hashFile = Path.Combine(SessionDirectory, "hashes.sha256");
        var lines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(SessionDirectory, "*", SearchOption.AllDirectories)
                     .Where(p => !string.Equals(p, hashFile, StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var relative = Path.GetRelativePath(SessionDirectory, file).Replace('\\', '/');
                lines.Add($"{Hashing.Sha256(File.ReadAllBytes(file))}  {relative}");
            }
            catch { }
        }
        File.WriteAllLines(hashFile, lines, Encoding.UTF8);
    }

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private static void EnsureDiagnosticsOutsideProviderDirectories(ProbeConfiguration config)
    {
        var diag = Path.GetFullPath(config.DiagnosticsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var provider in new[] { config.PecBaseDirectory, config.RequestDirectory, config.ResponseDirectory })
        {
            var p = Path.GetFullPath(provider).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (diag.StartsWith(p, StringComparison.OrdinalIgnoreCase) || p.StartsWith(diag, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("DiagnosticsRoot must be outside PEC provider directories.");
        }
    }
}

public static class DiagnosticExporter
{
    private static readonly HashSet<string> SafeAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        "session.json","environment.json","pec-services.json","configuration.json","directory-before.json","directory-after.json",
        "filesystem-events.jsonl","timeline.jsonl","observed-request-metadata.json","request-capture-stability.json","request-contract-analysis.json",
        "published-request-metadata.json","request-comparison.json","response-metadata.json","response-parsing.json","amount-analysis.json",
        "errors.jsonl","SUMMARY.md","hashes.sha256","evidence-inventory.json"
    };

    public static string ExportSafeBundle(string sessionDirectory, string? destinationZip = null)
    {
        destinationZip ??= Path.Combine(sessionDirectory, $"SAFE-{Path.GetFileName(sessionDirectory)}.zip");
        if (File.Exists(destinationZip)) File.Delete(destinationZip);

        using var archive = ZipFile.Open(destinationZip, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(sessionDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (!SafeAllowList.Contains(name)) continue;
            archive.CreateEntryFromFile(file, name, CompressionLevel.Optimal);
        }

        AddSanitizedRepresentationIfAvailable(archive, sessionDirectory, "observed-request.raw", "observed-request-sanitized.txt");
        AddSanitizedRepresentationIfAvailable(archive, sessionDirectory, "observed-response.raw", "observed-response-sanitized.txt");
        return destinationZip;
    }

    private static void AddSanitizedRepresentationIfAvailable(ZipArchive archive, string root, string rawName, string outputName)
    {
        var rawPath = Path.Combine(root, rawName);
        if (!File.Exists(rawPath)) return;
        var bytes = File.ReadAllBytes(rawPath);
        var evidence = EvidenceTextAnalyzer.Analyze(bytes);
        if (!evidence.DecodingSafe || evidence.Text is null) return;
        var sanitized = SensitiveDataSanitizer.Sanitize(evidence.Text);
        var entry = archive.CreateEntry(outputName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(sanitized.SanitizedText);
    }
}
