# SRWF-POS-PEC-Probe — First-Day Test Guide

## DO NOT START WITH PUBLISH

The primary purpose of this executable is to observe the real filesystem behavior produced by the externally supplied known-good PEC Tester before the Probe tries to reproduce it.

Use this sequence:

```text
OBSERVE
→ MEASURE
→ INFER CANDIDATE CONTRACT
→ REPRODUCE
→ VALIDATE
```

Do **not** use:

```text
GUESS
→ SEND MONEY
→ SEE IF IT WORKS
```

## First-day procedure

1. Install the PEC/EasySoft PC-POS Windows software/service using the supplied installer/instructions.
2. Connect the PEC terminal using the intended LAN or USB setup.
3. Run `SRWF-POS-PEC-Probe.exe`.
4. Confirm the banner says `PEC PROBE — TEST / EVIDENCE MODE`.
5. Keep mode set to **OBSERVE ONLY**.
6. Verify `PEC Base Directory`, `Request Directory`, and `Response Directory`. The defaults are candidate evidence, not official PEC facts.
7. Click **Refresh Preflight**.
8. Confirm the expected directories exist and review any candidate PEC/PCPOS service entries. The Probe will not create directories or start/stop/restart services.
9. Enter the amount you are about to type into the external Tester under **Tester Entered Amount**.
10. Select the unit the Tester visibly presents: `Toman`, `Rial`, or `Unknown`. This is operator-provided evidence, not PEC contract truth.
11. Click **Start Observation**.
12. Open the known-good external PEC Tester.
13. Enter one small transaction amount that you are permitted to test.
14. Execute the transaction once in the external Tester.
15. Allow the Probe to observe Request and Response directory activity.
16. If the Request disappears too quickly to establish stable content, accept `REQUEST_CONTENT_NOT_CAPTURED`, `REQUEST_CONTENT_DISAPPEARED_BEFORE_CONFIRMATION`, or another non-promotable capture status as truthful evidence. Do not repeat a payment merely to make the capture look successful.
17. After the Tester/terminal reaches a stable result, click **Stop Observation**.
18. Review **Observed Contract**: filename, encoding, BOM, newline form, field order, capture stability, publication pattern/evidence, amount relationship, confidence, and missing evidence.
19. Click **Export Safe Diagnostic Bundle** for a shareable ZIP. Raw Response is excluded by default.
20. Review the evidence before considering `PUBLISH`.
21. Normal `PUBLISH` remains locked unless the Probe has enough observed Tester evidence to reproduce the destination and Request structure, has bounded stable Request capture evidence, has sufficient publication evidence, and can mechanically verify the prepared Request preserves the observed structure. `TEMP_THEN_RENAME` also requires an observed temporary filename; direct publication requires positive create visibility rather than Changed-only inference.
22. If amount-unit evidence is unresolved, the Probe requires an additional explicit confirmation and performs **no Rial/Toman conversion**.
23. Enter a small **Raw Amount** not exceeding `MaxProbeRawAmount` (default candidate: `100000` raw units).
24. Select **PUBLISH** only after evidence review. `UNVERIFIED PUBLISH OVERRIDE` is an expert-only escape hatch and is logged as unverified. It does **not** bypass an unresolved prior publish attempt.
25. Click **Start PEC Probe** once for the controlled reproduction test.
26. Do not send a second Request while the outcome is `UNKNOWN`, candidate-only, or otherwise unresolved.
27. Export the updated Safe Diagnostic Bundle.
28. Keep physical validation status as `PENDING_REAL_PEC_VALIDATION` until the observed terminal/service behavior supports a stronger claim.

## What OBSERVE ONLY is allowed to do

OBSERVE ONLY may:

- enumerate Request/Response directories;
- watch all filenames;
- poll while armed (default 50 ms);
- open candidate files using non-exclusive read-sharing semantics;
- perform bounded repeated reads to determine whether captured Request bytes and relevant metadata are stable;
- copy captured bytes into the Probe's own local diagnostic directory;
- hash and analyze those copied bytes;
- record event/timing evidence.

OBSERVE ONLY must not:

- create PEC Request files;
- modify PEC Request files;
- modify PEC Response files;
- rename provider artifacts;
- delete provider artifacts;
- start a payment.

## PUBLISH safety gates

Normal PUBLISH is blocked when any of the following applies:

- no captured Tester Request exists;
- Request capture stability has not been established with at least two matching bounded samples and stable pre/post read metadata;
- conflicting Request samples were observed before observation finalization;
- Request content/encoding/amount field is not reproducible;
- publication method is not sufficiently observed;
- direct publication has only Changed activity and no positive final-path create evidence;
- a temp+rename strategy was observed but the temporary naming evidence or required final-path byte equivalence is missing;
- the prepared Request is `DIFFERENT` or `NOT_COMPARABLE` to the observed structure after allowing only explicitly replaced dynamic fields;
- no amount relationship was observed;
- Raw Amount exceeds `MaxProbeRawAmount`;
- any pre-existing Request file is present;
- any pre-existing Response file is present;
- durable diagnostics indicate a prior PUBLISH attempt remains unresolved, even after application restart;
- another Probe process owns the named mutex;
- another attempt is active/unresolved in the running process.

The recovery block is derived from persisted per-session diagnostic evidence. Empty PEC Request/Response directories do not prove that a prior attempt was safe to retry. Candidate Response evidence also does not automatically clear the block, and `UNVERIFIED PUBLISH OVERRIDE` cannot bypass it.

A timeout produces:

```text
UNKNOWN
```

It does not produce `FAILURE`, and it never starts an automatic retry.

## TEMP_THEN_RENAME failure behavior

If a temp-file publication fails after the temp pathname has been created or closed, the Probe does **not** automatically delete that pathname. At that point pathname ownership may be uncertain because an external actor could have replaced it. The failure is propagated and the attempt remains `RECOVERY_REQUIRED / UNKNOWN` for evidence-based review.

This deliberate no-cleanup rule is separate from the explicit post-session archive operation below.

## Explicit provider-artifact archive

`Acknowledge & Archive Provider Artifacts` is never automatic.

Use it only after the observation/attempt is inactive and after reviewing why the provider file remains.

For each file, the Probe:

1. records `PROVIDER_ARTIFACT_ARCHIVE_STARTED`;
2. captures exact bytes read-only;
3. stores evidence outside the PEC directories;
4. hashes the stored evidence;
5. re-reads/hashes the source;
6. aborts if the source changed;
7. attempts a move into `Diagnostics/<session>/provider-archive/`;
8. leaves the source untouched if the move cannot safely complete;
9. records `PROVIDER_ARTIFACT_ARCHIVED` or `PROVIDER_ARTIFACT_ARCHIVE_ABORTED`.

It never force-deletes the provider file.

## Troubleshooting

### Candidate PEC service not found

Possible explanations include:

- PEC/EasySoft service is not installed;
- the service uses an unexpected name/display name;
- installation is incomplete;
- the account cannot inspect all service metadata.

Do not infer from this result alone that PEC is unavailable.

### Service found but stopped

Verify the PEC/EasySoft installation and service configuration using the vendor's normal operational procedure. The Probe intentionally does not start, stop, or restart services.

### Request/Response directories are missing

Treat this as installation/configuration evidence. Verify the PEC/EasySoft setup and configured filesystem path. The Probe does not silently create these directories.

### Request event observed but content missed or unstable

If the service consumes a transient file before the Probe can read it sufficiently, the Probe may report:

```text
REQUEST_CONTENT_NOT_CAPTURED
REQUEST_CONTENT_DISAPPEARED_BEFORE_CONFIRMATION
REQUEST_CONTENT_UNSTABLE
```

The observed event and any raw samples remain useful evidence. They are not promoted to normal-PUBLISH contract evidence. Do not invent the missing/stable contents, and do not repeatedly send payments while the payment state is uncertain.

### Known-good Tester creates a Request but it is never consumed

Do not conclude a single root cause. Possible causes include:

- terminal not enabled/provisioned for PC-POS by PEC;
- terminal/service configuration mismatch;
- invalid connection type;
- LAN address/port issue;
- USB/service configuration issue;
- Request contract/version mismatch;
- filesystem permissions;
- disconnected or unavailable terminal.

If the known-good Tester also fails while the service appears running, expected directories exist, and the terminal is connected, ask PEC/EasySoft support whether PC-POS functionality is enabled/provisioned for that terminal. Provisioning is a **possible cause**, not a proven diagnosis.

### Response not received

After the operational timeout, treat the attempt as `UNKNOWN`. The Probe continues the configured late-response observation window and does not automatically retry.

### Stale Request or Response exists

PUBLISH is blocked with `RECOVERY_REQUIRED`. Review/capture the artifact. Never delete it merely to make the UI ready. If appropriate, use the explicit archive operation after the active session is stopped.

### Recovery remains required after restart even though PEC folders are empty

This is intentional when persisted session evidence shows that an earlier PUBLISH attempt crossed the durable pre-dispatch boundary. Folder absence does not prove whether money moved. Continue OBSERVE ONLY and preserve the diagnostics for reconciliation; do not use the expert override as a retry mechanism.

### PUBLISH remains locked

This is expected when evidence is incomplete. Capture a real Request from the known-good Tester first. Normal PUBLISH requires stable Request capture and sufficient observed publication evidence. If the content or publication pattern cannot be observed sufficiently, keep normal PUBLISH locked. `UNVERIFIED PUBLISH OVERRIDE` remains explicitly unverified and cannot override an unresolved prior attempt.

## Safe diagnostic handling

Local raw evidence may contain unknown payment-related identifiers. Keep the local diagnostic directory protected.

The Safe ZIP excludes raw Response by default and uses conservative sanitization for shareable text. Sanitization is not treated as a guarantee for an unknown provider schema; exclusion of raw Response is the primary boundary.
