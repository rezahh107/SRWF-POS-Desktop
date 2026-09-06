using System.Text.Json.Serialization;

namespace SRWF.POS.PecProbe.Core;

public enum ProbeMode { ObserveOnly, Publish }
public enum TesterDisplayedUnit { Unknown, Toman, Rial }
public enum PublicationPattern
{
    Unknown,
    DirectCreateAndWrite,
    TempFileThenRename,
    ReplaceExisting,
    OtherObservedPattern
}
public enum PublicationStrategy { DirectWrite, TempThenRename, OtherObserved, UnverifiedOverride }
public enum CandidateSchemaMatch { MatchesExternalCandidate, DiffersFromExternalCandidate, InsufficientEvidence }
public enum AmountRelationship { Unknown, Equal, RequestEqualsTesterTimes10, RequestTimes10EqualsTester, Other }
public enum CaptureStatus { ContentCaptured, ContentNotCaptured }
public enum CaptureStability
{
    NotEstablished,
    Stable,
    Unstable,
    DisappearedBeforeConfirmation,
    ConflictingEvidence
}
public enum CandidateResponseOutcome
{
    CandidateSuccess,
    CandidateUserCancelled,
    CandidateInsufficientFunds,
    CandidateInvalidPin,
    UnmappedResponse,
    ParseUnknown
}

public sealed record ProbeConfiguration(
    string PecBaseDirectory,
    string RequestDirectory,
    string ResponseDirectory,
    string DiagnosticsRoot,
    int PollIntervalMilliseconds = 50,
    int OperationalTimeoutSeconds = 120,
    int LateResponseWindowSeconds = 300,
    long MaxProbeRawAmount = 100000)
{
    public static ProbeConfiguration CreateDefault(string diagnosticsRoot) => new(
        @"C:\Users\Public\PEC_PCPOS",
        @"C:\Users\Public\PEC_PCPOS\Request",
        @"C:\Users\Public\PEC_PCPOS\Response",
        diagnosticsRoot);
}

public sealed record OperatorObservationInput(long? TesterEnteredAmount, TesterDisplayedUnit TesterDisplayedUnit);

public sealed record FileSnapshot(
    string DirectoryRole,
    string FullPath,
    string FileName,
    long Length,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc,
    string? Sha256,
    bool HashReadable,
    string? ReadError = null);

public sealed record DirectorySnapshot(
    string DirectoryRole,
    string Path,
    bool Exists,
    bool Enumerable,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<FileSnapshot> Files,
    string? Error = null);

public sealed record FilesystemEventRecord(
    long Sequence,
    DateTimeOffset TimestampUtc,
    string DirectoryRole,
    string EventType,
    string FullPath,
    string FileName,
    string? OldFullPath = null,
    long? Length = null,
    string? Sha256 = null,
    string EvidenceStatus = "EVENT_OBSERVED",
    string? Note = null);

public sealed record ArtifactReadSample(
    int SampleNumber,
    DateTimeOffset SampledAtUtc,
    DateTime CreationTimeUtcBefore,
    DateTime LastWriteTimeUtcBefore,
    long LengthBefore,
    DateTime CreationTimeUtcAfter,
    DateTime LastWriteTimeUtcAfter,
    long LengthAfter,
    byte[] RawBytes,
    string Sha256,
    bool MetadataStableAcrossRead);

public sealed record ArtifactCapture(
    string DirectoryRole,
    string FullPath,
    string FileName,
    DateTimeOffset CapturedAtUtc,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc,
    byte[] RawBytes,
    string Sha256,
    CaptureStatus Status = CaptureStatus.ContentCaptured,
    CaptureStability Stability = CaptureStability.NotEstablished,
    int SuccessfulSampleCount = 1,
    IReadOnlyList<ArtifactReadSample>? Samples = null)
{
    [JsonIgnore]
    public bool StabilityEstablished => Stability == CaptureStability.Stable && SuccessfulSampleCount >= 2;
}

public sealed record TextEvidence(
    string ProbableEncoding,
    bool BomPresent,
    string NewlineRepresentation,
    bool FinalNewlinePresent,
    bool DecodingSafe,
    string? Text,
    IReadOnlyList<string> FieldNames,
    IReadOnlyList<KeyValuePair<string, string>> Fields);

public sealed record ObservedRequestContract(
    string DestinationPath,
    string FileName,
    TextEvidence TextEvidence,
    CandidateSchemaMatch CandidateSchemaMatch,
    PublicationPattern PublicationPattern,
    string PublicationStrategyEvidence,
    string PublicationStrategyConfidence,
    string? ObservedTemporaryFileName,
    string? AmountFieldName,
    long? ObservedRawAmount,
    bool IsObservedTesterEvidence,
    byte[] ObservedRawBytes,
    bool CaptureStabilityEstablished = false,
    int StableSampleCount = 0,
    string CaptureStabilityEvidence = "NOT_ESTABLISHED",
    bool PublicationEvidenceEstablished = false)
{
    [JsonIgnore]
    public bool HasReproducibleStructure =>
        IsObservedTesterEvidence &&
        CaptureStabilityEstablished &&
        StableSampleCount >= 2 &&
        PublicationEvidenceEstablished &&
        TextEvidence.DecodingSafe &&
        !string.IsNullOrWhiteSpace(AmountFieldName) &&
        (PublicationPattern == PublicationPattern.DirectCreateAndWrite ||
         (PublicationPattern == PublicationPattern.TempFileThenRename && !string.IsNullOrWhiteSpace(ObservedTemporaryFileName)));
}

public sealed record AmountAnalysis(
    string Status,
    long? TesterEnteredAmount,
    TesterDisplayedUnit TesterDisplayedUnit,
    long? ObservedRequestAmount,
    AmountRelationship Relationship,
    string CandidateInterpretation,
    string Confidence,
    string ProofStatus = "AMOUNT_UNIT_NOT_PROVEN");

public sealed record ResponseParseResult(
    CandidateResponseOutcome Outcome,
    string? CandidateCode,
    string Label,
    int? TokenIndex,
    string EvidenceLevel = "CANDIDATE_MAPPING_ONLY");

public sealed record PublishGateInput(
    ObservedRequestContract? Contract,
    long RawAmount,
    long MaxProbeRawAmount,
    bool StaleRequestExists,
    bool StaleResponseExists,
    bool AmountRelationshipObserved,
    bool AmountUnitStillUnresolved,
    bool AdditionalUnresolvedUnitConfirmation,
    bool ExpertOverride,
    PublicationStrategy? ExpertOverrideStrategy = null,
    bool UnresolvedPriorAttempt = false,
    RequestComparison? PreparedRequestComparison = null);

public sealed record PublishGateResult(bool Allowed, string Status, IReadOnlyList<string> Reasons);

public sealed record RequestRenderResult(byte[] Bytes, string Sha256, IReadOnlyList<string> ReplacedFields);

public sealed record RequestComparison(
    string Result,
    bool EncodingSame,
    bool BomSame,
    bool NewlineSame,
    bool FieldOrderSame,
    bool FinalNewlineSame,
    bool ByteIdentical,
    string ObservedSha256,
    string PreparedSha256,
    IReadOnlyList<string> Notes);

public sealed record RecoveryFinding(
    string SessionDirectory,
    string EvidenceSource,
    string Status,
    string? Detail = null);

public sealed record RecoveryAssessment(
    bool RecoveryRequired,
    string Status,
    IReadOnlyList<RecoveryFinding> Findings);

public sealed record SanitizationFinding(
    int TokenIndex,
    int OriginalLength,
    string CharacterClass,
    string StableHash,
    string RedactionReason,
    string Placeholder);

public sealed record SanitizationResult(string SanitizedText, IReadOnlyList<SanitizationFinding> Findings);

public sealed record DiagnosticSessionMetadata(
    string SessionId,
    DateTimeOffset StartedAtUtc,
    string PecAdapterStatus,
    string ProductionValidation,
    ProbeMode InitialMode,
    OperatorObservationInput OperatorInput,
    string Status = "ACTIVE");

public sealed record ArchiveOperationResult(string SourcePath, string Status, string? ArchivePath, string? Sha256, string? Error = null);

public enum FakePecScenario
{
    ExternalTesterCreatesRequestThenServiceConsumes,
    DirectRequestWrite,
    TempFileThenRenameRequest,
    RequestDisappearsTooQuicklyToCapture,
    SuccessfulLookingResponse,
    CancelledLookingResponse,
    MalformedResponse,
    RequestNeverConsumed,
    RequestConsumedButResponseNeverAppears,
    DelayedResponse,
    StaleResponseAlreadyPresent
}
