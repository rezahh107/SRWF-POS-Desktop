# PEC Probe Evidence Register

Status: **P0 EVIDENCE COLLECTION OPEN**

```text
PEC_ADAPTER_STATUS = IMPLEMENTED_AGAINST_EXTERNAL_EVIDENCE
PRODUCTION_VALIDATION = PENDING_REAL_PEC_VALIDATION
PEC_PROTOCOL_STATUS = INCOMPLETE
```

This document separates evidence from interpretation. A claim may be upgraded only within the scope actually observed. One transaction does not prove a universal PEC protocol.

## Evidence classes

| Class | Meaning | Current authority |
|---|---|---|
| `EXTERNAL_IMPLEMENTATION_EVIDENCE` | Behavior/schema shown by a public or third-party implementation | Candidate-only; not official PEC contract |
| `OBSERVED_TESTER_EVIDENCE` | Filesystem behavior captured while the known-good external Tester is used | Direct observation of that Tester/environment only |
| `OBSERVED_REAL_PEC_EVIDENCE` | Request/Response/device/service behavior captured in a controlled physical PEC transaction | Strong P0 evidence for the exact tested environment |
| `OWNER_REPORTED_INPUT` | Amount/unit/configuration entered or reported by the operator | Useful correlation evidence; not provider truth by itself |
| `DERIVED_CANDIDATE_CONTRACT` | Structure inferred from one or more observations | Reproducible hypothesis, still subject to validation |
| `OFFICIAL_PEC_CONTRACT` | Provider-authoritative contract with independently established provenance | **NOT PRESENT IN THIS REPOSITORY** |

The EasySoft-provided/known-good Tester is not labelled `OFFICIAL_PEC_CONTRACT` unless provenance independently establishes that status.

## Current external candidate evidence

External implementation evidence suggests a base directory and candidate files similar to:

```text
C:\Users\Public\PEC_PCPOS
Request\TransAction.txt
Response\TransAction.txt
```

and a candidate Request shape:

```text
Amount=<amount>
type=<type>
IP=<ip>
port=<port>
```

The Probe may compare observations against that shape, but observation wins if the actual Tester differs.

Candidate first-line/third-token Response mappings are implemented only as analysis labels:

```text
00 -> Candidate Success
99 -> Candidate User Cancelled
51 -> Candidate Insufficient Funds
55 -> Candidate Invalid PIN
```

Unknown codes remain `UNMAPPED_RESPONSE`; missing third token remains `PARSE_UNKNOWN`.

## Claims that remain unverified

The following remain explicitly open until evidence closes a specific item:

- complete official Request schema;
- complete official Response schema;
- authoritative amount unit;
- encoding contract;
- BOM contract;
- newline contract;
- final-newline contract;
- publication atomicity;
- temporary-file naming/rename semantics;
- complete response-code mapping;
- USB semantics;
- production transaction timeout;
- late-response semantics;
- stale-file behavior;
- correlation guarantees;
- service/version differences;
- cleanup/archive rules accepted by PEC;
- whether Request disappearance implies anything about payment outcome.

## Evidence upgrade rules

1. Raw bytes are preserved before parsing or normalization.
2. Filesystem events and captures are timestamped and hashed.
3. Operator-entered Tester amount/unit is stored as `OWNER_REPORTED_INPUT`.
4. A measured amount relationship may become `AMOUNT_RELATION_OBSERVED` but remains `AMOUNT_UNIT_NOT_PROVEN` until stronger evidence exists.
5. A detected write/rename pattern is a candidate publication behavior for the tested Tester/service version; it is not universal PEC protocol authority.
6. Response parsing never upgrades a candidate code to official semantics by itself.
7. Physical PEC evidence must record the terminal/service/environment version and visible outcome.
8. Conflicting observations lower confidence; they are not normalized away.

## Evidence expected from the first real observation session

- pre-session Request/Response directory snapshots;
- ordered `FileSystemWatcher` + polling timeline;
- Request event presence even if content is missed;
- exact Request bytes when captured;
- SHA-256, byte length, timestamps;
- probable encoding/BOM/newline/final-newline information;
- field order without forcing candidate schema;
- publication-pattern inference and confidence;
- operator-entered Tester amount/unit;
- observed Request amount candidate and relation analysis;
- exact Response bytes before parsing when captured;
- candidate response parsing output;
- diagnostic summary and safe export.

## Production closure

Do not report `PEC integration verified` from this POC alone.

The production master specification still requires a real-device POC and a future evidence-backed `docs/PEC-POC-PROTOCOL.md` before PEC protocol closure.
