# SRWF-POS-Desktop

This repository currently contains the **SRWF-POS-PEC-Probe** P0 evidence-gathering POC plus the pre-POC master specification.

> **PEC PROBE — TEST / EVIDENCE MODE**
>
> `OBSERVE → MEASURE → INFER CANDIDATE CONTRACT → REPRODUCE → VALIDATE`

The Probe is not the production POS application and does not claim an official PEC protocol contract.

```text
PEC_ADAPTER_STATUS = IMPLEMENTED_AGAINST_EXTERNAL_EVIDENCE
PRODUCTION_VALIDATION = PENDING_REAL_PEC_VALIDATION
```

## Safety position

- `OBSERVE ONLY` is the default mode.
- `OBSERVE ONLY` does not create, modify, rename, archive, or delete files inside the configured PEC Request/Response directories.
- `PUBLISH` is locked until observed Tester evidence is sufficient, unless the operator explicitly selects `UNVERIFIED PUBLISH OVERRIDE`.
- No automatic retry or resend exists.
- Timeout means `UNKNOWN`, not payment failure.
- Existing provider Request/Response artifacts block dispatch with `RECOVERY_REQUIRED`.
- Provider artifacts are never automatically deleted.
- Raw amount is never automatically converted between Rial and Toman.
- Safe diagnostic export excludes raw Response evidence by default.

Read `SRWF-POS-Desktop-MASTER-SPEC-v0.1.1.md` first. The master specification keeps the PEC contract open until real-device P0 evidence exists.

## Projects

```text
SRWF-POS-Desktop.sln
src/SRWF.POS.PecProbe.Core/    # net10.0, platform-independent evidence/safety logic
src/SRWF.POS.PecProbe.App/     # net10.0-windows WinForms application
tests/SRWF.POS.PecProbe.Tests/ # net10.0 Core tests
docs/PEC-PROBE-TEST-GUIDE.md
docs/PEC-PROBE-EVIDENCE.md
```

## Build and test

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

Windows x64 publish:

```powershell
dotnet publish src/SRWF.POS.PecProbe.App \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -o artifacts/win-x64
```

Expected executable:

```text
artifacts/win-x64/SRWF-POS-PEC-Probe.exe
```

Trimming is intentionally disabled for this WinForms POC.

## First run

**DO NOT START WITH PUBLISH.** Follow `docs/PEC-PROBE-TEST-GUIDE.md` and begin with the known-good external PEC Tester in `OBSERVE ONLY` mode.
