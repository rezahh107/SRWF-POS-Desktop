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
16. If the Request disappears too quickly to read, accept `REQUEST_CONTENT_NOT_CAPTURED` as truthful evidence. Do not repeat a payment merely to make the capture look successful.
17. After the Tester/terminal reaches a stable result, click **Stop Observation**.
18. Review **Observed Contract**: filename, encoding, BOM, newline form, field order, publication pattern, amount relationship, confidence, and missing evidence.
19. Click **Export Safe Diagnostic Bundle** for a shareable ZIP. Raw Response is excluded by default.
20. Review the evidence before considering `PUBLISH`.
21. Normal `PUBLISH` remains locked unless the Probe has enough observed Tester evidence to reproduce the destination, Request structure, amount field, and publication method. Temp+rename also requires an observed temporary filename.
22. If amount-unit evidence is unresolved, the Probe requires an additional explicit confirmation and performs **no Rial/Toman conversion**.
23. Enter a small **Raw Amount** not exceeding `MaxProbeRawAmount` (default candidate: `100000` raw units).
24. Select **PUBLISH** only after evidence review. `UNVERIFIED PUBLISH OVERRIDE` is an expert-only escape hatch and is logged as unverified.
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
- Request content/encoding/amount field is not reproducible;
- publication method is not sufficiently observed;
- a temp+rename strategy was observed but the temporary naming evidence is missing;
- no amount relationship was observed;
- Raw Amount exceeds `MaxProbeRawAmount`;
- any pre-existing Request file is present;
- any pre-existing Response file is present;
- another Probe process owns the named mutex;
- another attempt is active/unresolved in the running process.

A timeout produces:

```text
UNKNOWN
```

It does not produce `FAILURE`, and it never starts an automatic retry.

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

### Request event observed but content missed

If the service consumes a transient file before the Probe can read it, the Probe reports:

```text
REQUEST_CONTENT_NOT_CAPTURED
```

The event is still useful. Do not invent the Request contents, and do not repeatedly send payments while the payment state is uncertain.

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

### PUBLISH remains locked

This is expected when evidence is incomplete. Capture a real Request from the known-good Tester first. If the content or publication pattern cannot be observed sufficiently, keep PUBLISH locked unless a qualified operator deliberately uses `UNVERIFIED PUBLISH OVERRIDE` and accepts that the behavior is not proven equivalent.

## Safe diagnostic handling

Local raw evidence may contain unknown payment-related identifiers. Keep the local diagnostic directory protected.

The Safe ZIP excludes raw Response by default and uses conservative sanitization for shareable text. Sanitization is not treated as a guarantee for an unknown provider schema; exclusion of raw Response is the primary boundary.
