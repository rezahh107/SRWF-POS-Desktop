# SRWF POS Desktop — Master Specification

**Repository:** `SRWF-POS-Desktop`  
**Document ID:** `SRWF-POS-DESKTOP-MASTER`  
**Version:** `0.1.1-draft`  
**Status:** `DESIGN_BASELINE — P0_REQUIRED_BEFORE_IMPLEMENTATION`  
**Primary platform:** Windows  
**Implementation baseline:** C# / .NET 10 LTS / WinForms / SQLite

---

## 1. Purpose

`SRWF-POS-Desktop` is a small, deterministic Windows payment utility intended to:

1. collect the financial header needed at registration time;
2. calculate the payable amount deterministically;
3. initiate a payment on a supported PC-POS terminal, initially PEC (تجارت الکترونیک پارسیان);
4. capture and persist the observed payment result;
5. maintain a complete local audit history of payment intents, attempts, state transitions, and raw provider responses;
6. export the final result as a versioned JSON payload to the Windows clipboard so it can be pasted into SRWF / Gravity Forms;
7. preserve ambiguous or interrupted transactions for later reconciliation instead of guessing their outcome.

This application is deliberately small. It is **not** an accounting system, a replacement for SRWF, a banking switch, or an implementation of the payment-network protocol.

---

## 2. Product boundary

### 2.1 In scope

The desktop application SHALL provide:

- Entry / پرونده reference input.
- Tuition amount input.
- Discount amount input.
- Discount title input.
- Deterministic calculation of net payable amount.
- Payment amount input, defaulting to the remaining/net amount.
- “Send to POS” action.
- PEC PC-POS adapter through the official PEC Windows Service/file interface, subject to POC confirmation.
- Explicit transaction state machine.
- SQLite local database.
- Full transaction history.
- Append-only audit events.
- Storage of raw observed PEC request/response evidence.
- Startup recovery and stale-artifact detection.
- Retry only when the previous outcome is known to be safe to retry.
- “Copy last result” action.
- Automatic clipboard copy after a confirmed result, with verification.
- “Needs Review” view for unresolved transactions.
- Search/filter of historical transactions.
- Local configuration for proven PEC parameters.
- Deterministic and testable core logic.

### 2.2 Out of scope for the first implementation

The first implementation SHALL NOT:

- call Shaparak or bank APIs directly;
- implement card-network protocols;
- reverse engineer the POS terminal protocol when an official PEC component is available;
- store PAN/full card number, PIN, track data, CVV, or other sensitive cardholder authentication data;
- perform online Sayad cheque inquiry;
- serve as the authoritative accounting ledger for the organization;
- automatically resolve an ambiguous banking outcome by guess;
- automatically retry an `UNKNOWN` transaction;
- hard-delete a payment attempt after external dispatch may have occurred;
- treat a local “void” action as a banking refund/reversal;
- assume undocumented PEC response codes are authoritative.

---

## 3. Current architectural decision

The initial integration target is the PEC PC-POS model documented for تجارت الکترونیک پارسیان.

Available evidence confirms that the PEC integration can use a Windows Service and that the service creates fixed `Request` and `Response` directories under:

```text
C:\Users\Public\PEC_PCPOS\
```

The evidence also confirms that supported connection modes include network, USB, network via Windows Service, and USB via Windows Service, and that software can send the invoice amount to the POS.

A public implementation demonstrates a file named `TransAction.txt` and a request containing fields such as amount, connection type, IP, and port. That implementation is **reference evidence only**, not the authoritative PEC protocol specification.

Therefore:

> Exact request syntax, complete response syntax, response-code semantics, timing behavior, correlation capability, and failure behavior remain `NOT_PROVEN` until a real-device POC is completed.

---

## 4. Determinism contract

“100% deterministic” applies to the application core, not to the external banking network.

The external environment contains non-deterministic factors:

- POS hardware;
- PEC service;
- LAN / USB;
- network availability;
- issuing bank;
- user actions;
- power loss;
- process or Windows restart.

The application SHALL instead guarantee:

```text
same persisted state
+ same observed external event
+ same configuration/version
= same next state and same allowed actions
```

The application SHALL NEVER convert missing knowledge into a fabricated `SUCCESS` or `FAILED`.

`UNKNOWN` is a first-class valid state.

### 4.1 Deterministic invariants

1. All authoritative monetary values are stored as signed 64-bit integers in **IRR**.
2. Toman exists only at the human UI boundary; Core, database, provider adapter, audit events, and export contracts use IRR.
3. Conversion from UI Toman to Core IRR occurs exactly once at the UI/application boundary using checked arithmetic.
4. Before dispatch, the operator must see and confirm the exact payment amount in **both IRR and Toman**.
5. Monetary arithmetic uses checked operations.
6. State transitions are explicit and versioned.
7. A payment/attempt record is persisted before external dispatch.
8. A terminal may have at most one blocking/in-flight attempt.
9. A named Windows system mutex prevents multiple application instances from operating the same local terminal context.
10. No automatic POS retry exists.
11. `UNKNOWN`, `RECOVERY_REQUIRED`, and `UNMAPPED_RESPONSE` never expose a normal retry action.
12. Raw provider evidence is preserved.
13. No observed provider response is silently discarded.
14. A post-dispatch attempt is never hard-deleted.
15. `SUCCESS` remains immutable as an observed provider outcome.
16. Provider outcome and business verification are separate concepts.
17. Unrecognized provider responses are not silently mapped to a known failure.
18. Clipboard failure never changes the payment result.
19. The database is the local operational/audit source; clipboard is only a transfer mechanism.
20. SQLite durability settings are explicit and verified on startup.
21. A late provider response may resolve an `UNKNOWN` only while no newer attempt has been started for that terminal.
22. Corrections to attribution fields such as `entry_ref` are append-only audited corrections, never silent rewrites.
23. Automated backups are mandatory and must target storage independent from the primary database location.

## 5. Monetary model

### 5.1 Canonical representation

Canonical unit:

```text
IRR — integer, Int64 / long
```

Example:

```text
65,000,000 تومان
= 650,000,000 ریال
```

The Core, SQLite schema, PEC adapter, audit log, and clipboard payload SHALL use IRR only.

### 5.2 UI boundary

The default human-facing input unit is **Toman**.

The conversion rule is:

```text
ui_toman
    ↓ checked × 10 exactly once
core_irr
```

No later layer may multiply or divide by 10 implicitly.

A value that has crossed the UI boundary is tagged by type/contract as IRR and must never be reinterpreted as Toman.

Before any external dispatch, the application SHALL show a mandatory confirmation surface:

```text
مبلغ ارسالی به کارت‌خوان

650,000,000 ریال
(65,000,000 تومان)

[ تأیید و ارسال ]
```

The operator must explicitly confirm this value before the PEC request can be published.

### 5.3 Calculations

```text
net_payable_irr = tuition_irr - discount_irr
```

During the standalone desktop phase, any displayed remaining amount is explicitly **local-context only**:

```text
local_remaining_irr =
    net_payable_irr
    - locally_known_successful_payment_total_irr
```

It is not authoritative for the full SRWF registration if payments may also exist as cheques, on another workstation, or in another system.

Validation:

```text
tuition_irr >= 0
discount_irr >= 0
discount_irr <= tuition_irr

if discount_irr > 0:
    discount_title is required

payment_amount_irr > 0
payment_amount_irr <= locally_known_remaining_irr
```

If the operational scope is deliberately restricted to “full single POS payment”, `payment_amount_irr` may default to the full local remaining amount. The data model SHALL nevertheless retain a distinct payment amount so partial/combined payment support does not require schema replacement later.

### 5.4 Provider amount verification

If the verified PEC response contract exposes the actual processed amount, the application SHALL compare it against the requested amount.

A provider-declared successful transaction with an amount mismatch remains a historical provider success but is **not business-accepted automatically**.

Example:

```text
provider_outcome      = SUCCESS
business_verification = AMOUNT_MISMATCH
workflow_disposition  = REVIEW_REQUIRED
```

The program SHALL NOT rewrite such a transaction as `FAILED`, because money may in fact have moved.

If PEC does not return a trustworthy transaction amount, the verification state is:

```text
AMOUNT_NOT_AVAILABLE
```

and no synthetic comparison is made.

## 6. User interface

The first usable UI SHOULD contain four surfaces.

### 6.1 Payment

```text
Entry / پرونده Ref
[______________]

Tuition
[______________] تومان

Discount
[______________] تومان

Discount title
[____________________________]

Net payable
65,000,000 تومان       [read-only]

Payment amount
[65,000,000] تومان

POS status
Ready

[ Send to POS ]
```

Before the request is actually published, a second confirmation step SHALL show:

```text
مبلغ ارسالی به کارت‌خوان
650,000,000 ریال
(65,000,000 تومان)

Entry Ref: 4817

[ تأیید و ارسال ]
[ انصراف ]
```

After a result:

```text
Provider result: SUCCESS / ...
Business verification: VERIFIED / REVIEW_REQUIRED / ...
Last observed result: ...

[ Copy last result ]
```

### 6.2 History

Columns should include at minimum:

- Entry Ref
- Payment ID
- Attempt number
- Amount
- Current state
- Provider response code if available
- Started time
- Completed/resolved time
- Review flag

Historical detail view SHOULD expose:

- financial snapshot;
- all attempts;
- event timeline;
- raw request evidence;
- raw response evidence;
- app version;
- reconciliation information.

### 6.3 Needs Review

The application SHALL visibly surface unresolved records:

```text
⚠ 2 transactions need review
```

Candidate records:

- `UNKNOWN`
- `RECOVERY_REQUIRED`
- `UNMAPPED_RESPONSE`
- stale provider artifact
- interrupted dispatch

The application SHOULD make this visible at startup whenever the queue is non-empty.

### 6.4 Settings

Only settings proven necessary by the PEC POC should be exposed.

Candidate settings include:

- provider adapter;
- PEC path;
- connection mode;
- device IP / port where required;
- transaction timeout;
- log retention;
- backup destination and retention policy.

The backup destination must not be the same physical failure domain as the primary database when a separate storage target is available.

Unknown/undocumented provider values SHALL NOT be invented.

## 7. Local database

SQLite is the default database.

The database must be created before production use and treated as part of the financial audit surface.

### 7.1 `payments`

Represents the payment intent/business context.

Candidate fields:

```text
payment_id                 TEXT PRIMARY KEY

entry_ref_original         TEXT NOT NULL
entry_ref_current          TEXT NOT NULL

tuition_irr                INTEGER NOT NULL
discount_irr               INTEGER NOT NULL
discount_title             TEXT
net_payable_irr            INTEGER NOT NULL
requested_payment_irr      INTEGER NOT NULL

current_state              TEXT NOT NULL
created_at_utc             TEXT NOT NULL
updated_at_utc             TEXT NOT NULL
app_version_created        TEXT NOT NULL
```

`entry_ref_original` is immutable.

`entry_ref_current` is an operational projection that may change only through an audited correction event containing the old value, new value, reason, operator identity, machine identity, and timestamp.

### 7.2 `payment_attempts`

One row per actual attempt.

```text
attempt_id                    TEXT PRIMARY KEY
payment_id                    TEXT NOT NULL
attempt_no                    INTEGER NOT NULL

provider                      TEXT NOT NULL
terminal_ref                  TEXT NOT NULL

state                         TEXT NOT NULL
provider_outcome              TEXT
business_verification         TEXT

requested_amount_irr          INTEGER NOT NULL
provider_amount_irr           INTEGER

created_at_utc                TEXT NOT NULL
dispatch_started_at_utc       TEXT
request_observed_at_utc       TEXT
response_observed_at_utc      TEXT
app_observed_completed_at_utc TEXT

provider_transaction_at_raw   TEXT
provider_transaction_at_utc   TEXT

raw_request                   TEXT
raw_response                  TEXT
response_code                 TEXT
response_mapping_version      TEXT

resolution_method             TEXT
resolution_note               TEXT
resolved_at_utc               TEXT

operator_windows_identity     TEXT NOT NULL
machine_name                  TEXT NOT NULL
app_version                   TEXT NOT NULL
```

Constraints SHALL enforce:

- unique `(payment_id, attempt_no)`;
- unique `attempt_id`;
- no invalid negative monetary values;
- one blocking/in-flight attempt per configured terminal.

A database backstop for terminal single-flight SHALL use a unique partial index equivalent to:

```sql
CREATE UNIQUE INDEX ux_terminal_blocking_attempt
ON payment_attempts(terminal_ref)
WHERE state IN (
    'DISPATCHING',
    'AWAITING_RESULT',
    'UNKNOWN',
    'RECOVERY_REQUIRED',
    'UNMAPPED_RESPONSE'
);
```

The exact blocking state list must remain synchronized with the versioned state-machine definition.

### 7.3 `payment_events`

Append-only audit stream.

```text
event_id
payment_id
attempt_id
sequence_no
event_type
payload_json
created_at_utc
app_version
```

Example:

```text
001 PAYMENT_CREATED
002 ATTEMPT_CREATED
003 DISPATCH_STARTED
004 REQUEST_FILE_PUBLISHED
005 REQUEST_CONSUMPTION_OBSERVED
006 RESPONSE_FILE_OBSERVED
007 RESPONSE_CAPTURED
008 RESPONSE_MAPPED
009 ATTEMPT_SUCCEEDED
010 CLIPBOARD_COPY_SUCCEEDED
```

`payment_events` is the audit truth. `current_state` fields are operational projections for UI/query convenience.

### 7.4 SQLite durability profile

Production connections SHALL explicitly establish and verify the durability profile:

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = FULL;
PRAGMA foreign_keys = ON;
```

`WAL + FULL` is selected because durability of committed transaction records across power loss matters more than marginal write throughput for this application.

The application SHALL verify the effective PRAGMA values at startup and fail closed for payment dispatch if the required durability profile cannot be established.

The SQLite database file and its WAL state must be treated consistently. Backups SHALL use the SQLite backup API / `Microsoft.Data.Sqlite.BackupDatabase()` rather than naïvely copying only the main `.db` file while the database is active.


---

### 7.5 Process-level single-instance protection

Database constraints are not the only single-flight defense.

The application SHALL acquire a named Windows system mutex before enabling payment operations. A second application instance that cannot acquire the mutex SHALL not enter an operational payment mode.

The mutex is a process-level guard; the SQLite unique partial index remains the authoritative data-level backstop.

---

## 8. Persistence-before-dispatch rule

The application SHALL create and commit local evidence before any external action that might move money.

Nominal ordering:

```text
1. Validate input
2. Create/commit Payment if needed
3. Create/commit Attempt in CREATED
4. Transition/commit Attempt to DISPATCHING
5. Publish PEC request
6. Observe provider behavior
7. Capture provider response evidence
8. Persist evidence
9. Map evidence to deterministic application state
10. Copy export payload to clipboard only after persistence
```

Important limitation:

SQLite and the PEC filesystem are separate durability domains. There is no atomic transaction spanning both.

Therefore a crash near the file-publication boundary can create an ambiguous recovery condition.

Example:

```text
DB says DISPATCHING
Request no longer exists
No definitive Response exists
```

This MUST NOT be interpreted as “not sent”.

It becomes:

```text
RECOVERY_REQUIRED / UNKNOWN
```

until evidence resolves it.

---

## 9. PEC file safety

Until the POC proves otherwise:

- assume one fixed request file and one fixed response file;
- assume only one in-flight transaction per terminal;
- never overwrite an existing unresolved response;
- never delete a stale response on startup merely to make the system “ready”;
- preserve exact bytes/text of observed responses before any cleanup;
- treat provider artifacts as evidence;
- do not infer correlation guarantees that have not been demonstrated.

If compatible with the official service, request publication SHOULD use a safe write strategy that prevents the service from observing a partially written file. The exact mechanism (for example temporary file + atomic rename) must be validated against the real PEC service before adoption.

Response detection SHOULD NOT rely solely on filesystem notification events. File existence/content checks remain authoritative, with notification mechanisms used only as an optimization.

---

## 10. State model

The exact state names may be refined after the PEC POC, but the semantics are locked.

### 10.1 Attempt lifecycle

```text
CREATED
  ↓
DISPATCHING
  ↓
AWAITING_RESULT
  ├── SUCCESS
  ├── DEFINITIVE_FAILURE
  ├── CANCELLED
  ├── UNKNOWN
  ├── UNMAPPED_RESPONSE
  └── RECOVERY_REQUIRED
```

### 10.2 Provider outcome

**SUCCESS**  
Definitive provider evidence confirms success.

**DEFINITIVE_FAILURE**  
A response explicitly proven by the PEC contract/validated mapping to mean no successful payment occurred.

**CANCELLED**  
A validated provider outcome confirms cancellation before successful completion.

**UNKNOWN**  
The application cannot prove whether the payment completed.

**UNMAPPED_RESPONSE**  
A response exists, but its semantics are not yet safely mapped.

**RECOVERY_REQUIRED**  
Persisted state and external artifacts indicate an interrupted/ambiguous dispatch or startup condition requiring reconciliation.

### 10.3 Business verification

Provider outcome and business acceptance are orthogonal.

Candidate values:

```text
NOT_APPLICABLE
AMOUNT_MATCH
AMOUNT_MISMATCH
AMOUNT_NOT_AVAILABLE
REVIEW_REQUIRED
```

A transaction may therefore legitimately be:

```text
provider_outcome      = SUCCESS
business_verification = AMOUNT_MISMATCH
```

The banking fact remains success; the application blocks business acceptance until review.

Provider response-code mappings SHALL be versioned and evidence-backed.

Public sample mappings such as `00`, `99`, `51`, and `55` are candidate evidence only until validated with the actual PEC environment or official specification.

## 11. Retry policy

A “retry” is always a **new attempt row**.

It never rewrites the previous attempt.

| Previous outcome | New attempt allowed? |
|---|---|
| Request definitively never dispatched | Yes |
| Validated definitive decline | Yes |
| Validated cancellation | Yes |
| Success | No |
| Unknown | No |
| Unmapped response | No until resolved/mapped |
| Recovery required | No until reconciled |

The UI label SHOULD preferably be:

```text
Start New Attempt
```

rather than “retry”, because the audit history must preserve each attempt independently.

No automatic POS retry is allowed.

---

## 12. Delete, void, and correction policy

### Hard delete

Allowed only for a local draft that has never entered any external-dispatch attempt.

Once an attempt reaches a state where dispatch may have occurred:

```text
Hard Delete = prohibited
```

### Local void/archive

An erroneous local record may be marked/archived with:

```text
voided_at
void_reason
voided_by
```

provided doing so does not rewrite the actual provider outcome.

### Success

A successful banking event remains historically `SUCCESS`.

It cannot be “deleted” or converted to failure by a local UI action.

If a real banking refund/reversal later exists, it must be represented as a separate compensating/reversal record according to a validated provider mechanism.

---

## 13. Startup recovery

Before enabling “Send to POS”, the application SHALL perform recovery checks.

At minimum:

1. acquire the named process mutex;
2. open SQLite;
3. verify required SQLite durability PRAGMAs;
4. run the required database integrity/open checks;
5. find unresolved/in-flight attempts;
6. inspect expected PEC Request/Response locations;
7. capture any stale/unexpected response as evidence;
8. never silently delete unresolved provider artifacts;
9. block new dispatch when ambiguity may cause double charge;
10. surface items in “Needs Review”.

A stale response at startup is a recovery event, not a cleanup inconvenience.

If an unresolved attempt exists, the application must continue observing for a late PEC response according to the reconciliation policy before permitting a new attempt on that terminal.

## 14. Reconciliation

`UNKNOWN` cannot be escaped by guessing.

### 14.1 Late-response reconciliation

After the operational timeout moves an attempt to `UNKNOWN`, the application SHALL continue monitoring the PEC response location for a configurable recovery window or until explicit reconciliation.

A late response is valid recovery evidence only when:

- the terminal is still blocked by the unresolved attempt;
- no newer attempt has been started;
- the observed response can be associated safely enough under the validated PEC contract.

This is a direct reason the application forbids a new attempt while the terminal remains `UNKNOWN`.

A late definitive response may produce an audited resolution such as:

```text
UNKNOWN
↓
LATE_RESPONSE_OBSERVED
↓
RECONCILED_SUCCESS
```

or another evidence-backed resolved outcome.

### 14.2 Human reconciliation

Where no authoritative late/automated evidence exists, reconciliation may use:

- printed POS receipt;
- terminal “last transaction” view;
- PSP/PEC support evidence;
- settlement/report evidence.

A reconciliation must record:

```text
resolved_by_windows_identity
resolved_machine
resolved_at_utc
resolution_method
resolution_note
```

Future versions may support automated evidence-based reconciliation if PEC exposes an official inquiry/last-transaction capability with sufficient correlation.

### 14.3 Entry reference correction

A wrong `entry_ref` does not alter the banking transaction.

Correction SHALL be represented by an append-only audit event:

```text
ENTRY_REF_CORRECTED
old_entry_ref
new_entry_ref
reason
operator_windows_identity
machine_name
corrected_at_utc
```

The original reference remains permanently stored. The current reference is only an operational projection for display/export.

## 15. Clipboard export contract

Clipboard is a convenience transport to SRWF, not the financial source of truth.

Payload format:

- UTF-8 compatible JSON;
- single logical JSON object;
- explicit schema version;
- immutable attempt identifier;
- current Entry reference;
- original Entry reference;
- amounts in IRR;
- provider outcome;
- business verification;
- separate application-observed and provider-reported timestamps when available.

Example:

```json
{
  "v": 1,
  "provider": "PEC",
  "entry_ref": "4817",
  "entry_ref_original": "4817",
  "payment_id": "P-...",
  "attempt_id": "A-...",
  "tuition_irr": 700000000,
  "discount_irr": 50000000,
  "discount_title": "تخفیف دانش‌آموز ممتاز",
  "net_payable_irr": 650000000,
  "payment_amount_irr": 400000000,
  "provider_amount_irr": 400000000,
  "provider_outcome": "SUCCESS",
  "business_verification": "AMOUNT_MATCH",
  "provider_response_code": "00",
  "app_observed_at_utc": "2026-09-05T19:12:42Z",
  "provider_transaction_at": null
}
```

Fields such as trace number, RRN, terminal ID, masked card, provider amount, and provider timestamp SHALL be added only if the actual PEC response exposes them and their semantics are verified.

### 15.1 Timestamp contract

The program SHALL distinguish:

```text
app_observed_at_utc
```

from:

```text
provider_transaction_at
```

The first is the local application observation timestamp.

The second exists only if PEC returns a verified provider/device transaction timestamp. The raw provider value should also be retained when parsing or timezone semantics are uncertain.

### 15.2 Clipboard reliability

The sequence SHALL be:

```text
1. Persist payment result
2. Build payload
3. Try clipboard write on the required UI/STA context
4. Read back and verify exact expected content
5. If successful: show “Copied”
6. If failed: preserve result and show explicit copy failure
```

Clipboard copy SHOULD have a small bounded retry policy for temporary clipboard contention.

The UI SHALL always provide:

```text
Copy Last Result
```

and a selectable fallback text view.

A clipboard failure can never roll back or alter a successful payment record.

## 16. SRWF boundary

For the standalone POC/Beta, the Windows app may accept:

- tuition;
- discount;
- discount title;
- Entry Ref.

This is convenient for testing and immediate use.

However, these fields belong conceptually to the SRWF registration/finance domain, not to the POS hardware domain.

Long-term preferred boundary:

```text
SRWF
  = authoritative tuition / discount / discount title / payable / full remaining balance

Windows POS Utility
  = entry_ref / payment amount / POS attempt / provider result / local audit
```

During the standalone phase, any `local_remaining_irr` shown by the desktop application is **not authoritative for the complete registration**. It cannot know about cheques, payments recorded elsewhere, or payments made on another workstation unless those facts are explicitly synchronized into it.

Moving the authoritative financial header back to SRWF later must not require replacement of the POS transaction database or attempt state model.

## 17. Security, privacy, and operator attribution

The application SHALL:

- never store PIN;
- never store full card number/PAN;
- never store CVV/CVV2;
- never store magnetic stripe/track data;
- store masked card data only if returned and operationally necessary;
- restrict local database and logs to the operating Windows account / approved users;
- avoid sensitive values in generic diagnostic logs;
- keep raw provider response only to the extent necessary for audit and troubleshooting;
- separate human-readable logs from authoritative database evidence;
- avoid embedding provider credentials in source control.

### 17.1 Operator identity

The initial application does not require a separate application login.

Audit actions such as reconciliation, void/archive, and entry-reference correction SHALL automatically capture:

```text
Windows domain/user identity
Machine name
UTC timestamp
```

This identifies the Windows session/workstation, not necessarily a unique human if staff share a Windows account.

If individual accountability becomes a requirement, a separate operator identity mechanism must be added rather than pretending a shared Windows username identifies a person.

### 17.2 Backup requirement

The primary SQLite file is not itself a backup.

The application SHALL support an automated daily backup using the SQLite backup mechanism.

Requirements:

- backup destination independent from the primary database location/failure domain where operationally possible;
- configurable retention;
- backup result logged;
- failed backup visibly surfaced;
- at least one documented restore test completed before production;
- restore procedure documented for operators/administrators.

A production deployment with no tested backup/restore path is not complete.

## 18. Technology baseline

### Required baseline

- **C#**
- **.NET 10 LTS**
- **WinForms**
- **Microsoft.Data.Sqlite**
- **System.Text.Json**
- Windows x64 primary deployment

.NET 10 is selected because it is the current LTS baseline and has support into 2028.

### Packaging

Initial production preference:

```text
self-contained
single-file where practical
```

Native AOT is not an initial requirement. Reliability and diagnosability take priority over marginal startup/runtime optimization.

### 18.1 Process singleton

The application SHALL use a named Windows system mutex to prevent concurrent operational instances on the same configured terminal context.

A second instance may show a diagnostic message, but SHALL NOT enable payment dispatch.

---

## 19. Recommended repository structure

```text
SRWF-POS-Desktop/
├─ README.md
├─ LICENSE
├─ docs/
│  ├─ MASTER-SPEC.md
│  ├─ PEC-POC-PROTOCOL.md
│  ├─ STATE-MACHINE.md
│  └─ TEST-MATRIX.md
├─ src/
│  ├─ Srwf.Pos.Desktop/
│  ├─ Srwf.Pos.Core/
│  ├─ Srwf.Pos.Persistence/
│  └─ Srwf.Pos.Providers.Pec/
└─ tests/
   ├─ Srwf.Pos.Core.Tests/
   ├─ Srwf.Pos.Persistence.Tests/
   └─ Srwf.Pos.Pec.Tests/
```

The project may start as fewer assemblies if that is simpler. Separation is only justified where it improves deterministic testing and provider isolation.

---

## 20. Test requirements

### 20.1 Deterministic unit tests

Must cover:

- Toman → IRR boundary conversion;
- dual-unit confirmation formatting;
- IRR arithmetic;
- overflow behavior;
- discount validation;
- partial payment validation;
- state-transition table;
- invalid transition rejection;
- retry eligibility;
- JSON schema/version;
- clipboard payload generation;
- provider amount match/mismatch behavior;
- provider-success + business-review behavior;
- response mapping;
- unmapped response handling;
- database constraints and partial unique single-flight index;
- named-mutex duplicate-instance behavior;
- required SQLite PRAGMA verification;
- entry-reference correction event;
- late-response reconciliation;
- event sequencing.

### 20.2 Persistence/recovery tests

Must cover application crash simulations around:

- before DB commit;
- after attempt commit / before request publication;
- immediately after request publication;
- after request disappears;
- after response appears / before DB persistence;
- after DB result persistence / before clipboard copy;
- restart with `UNKNOWN` followed by a late response;
- restart with stale PEC Response present;
- backup creation and restore validation.

### 20.3 Real PEC acceptance matrix

Before production release, execute on the actual terminal/service:

- successful transaction;
- customer cancellation;
- wrong PIN;
- insufficient funds if safely testable;
- POS unavailable;
- PEC Windows Service stopped;
- LAN/USB unavailable;
- interruption during transaction;
- stale Response at startup;
- app restart during an active attempt;
- response delay beyond normal duration;
- any provider code not previously mapped.

For every case preserve:

- exact observed Request;
- exact observed Response;
- timestamps;
- visible POS result;
- printed receipt where available;
- resulting application state.

---

## 21. PEC POC gate — mandatory before protocol closure

No production PEC adapter is considered complete until a real-device POC establishes the contract.

### Required POC

Use the official/known-good PEC tester and a small permissible real transaction.

Capture:

1. initial Request/Response directory state;
2. exact Request bytes/text;
3. time request becomes visible;
4. time request is consumed/disappears, if applicable;
5. exact Response bytes/text;
6. time response appears;
7. POS-visible outcome;
8. printed receipt;
9. successful, cancelled, and representative failure cases;
10. behavior after service/app restart;
11. behavior with a pre-existing Response file.

### Gate output

Create:

```text
docs/PEC-POC-PROTOCOL.md
```

with:

- verified request schema;
- verified response schema;
- verified encoding/newline rules;
- verified amount unit;
- verified response-code mappings;
- timeout observations;
- stale-file behavior;
- correlation evidence or confirmed absence;
- cleanup rules;
- device/service version information.

Until this document exists:

```text
PEC_PROTOCOL_STATUS = INCOMPLETE
```

---

## 22. Locked decisions

The following are currently treated as design locks unless new evidence requires revision:

- Windows desktop utility.
- C# / .NET 10 LTS.
- WinForms.
- SQLite local database.
- UI accepts Toman; Core/DB/provider contracts use Int64 IRR.
- Toman → IRR conversion occurs exactly once at the UI boundary.
- Mandatory dual-unit confirmation before POS dispatch.
- Checked financial arithmetic.
- Payment + Attempt model.
- Append-only event audit.
- Persist before external dispatch.
- `journal_mode=WAL` + `synchronous=FULL` production durability profile.
- Named Windows system mutex for process-level singleton protection.
- Database partial unique index for terminal blocking states.
- Single blocking/in-flight attempt per terminal.
- No automatic retry.
- No retry from UNKNOWN / recovery-required / unmapped states.
- Continue monitoring for a late PEC response after timeout while the terminal remains blocked.
- No hard-delete after possible dispatch.
- SUCCESS remains historical provider truth.
- Provider outcome and business verification are separate.
- Provider success with amount mismatch becomes review-required, not failed.
- UNKNOWN is first-class.
- Audited `ENTRY_REF_CORRECTED` instead of silent overwrite.
- Versioned JSON clipboard payload.
- `entry_ref`, `payment_id`, and `attempt_id` included in export.
- Separate application-observed and provider-reported timestamps.
- Clipboard verified after write.
- Automated backup + tested restore required for production.
- PEC provider isolated behind an adapter.
- No raw POS/payment-network protocol implementation when official PEC service can be used.
- PEC contract remains open until real POC.

## 23. Open decisions / evidence gaps

The following MUST NOT be guessed:

- exact PEC Request format;
- exact PEC Response format;
- complete provider fields returned;
- exact response-code semantics;
- whether response contains trace/RRN/terminal/masked-card data;
- whether response contains an authoritative processed amount;
- whether response contains an authoritative provider/device transaction timestamp;
- correlation identifier support;
- authoritative amount unit as exposed by the PEC file contract;
- safe response cleanup behavior;
- safe request-publication method;
- production transaction timeout;
- late-response observation window;
- official last-transaction/inquiry capability;
- exact reconciliation procedure;
- final placement of tuition/discount authority when integrated with SRWF.

## 24. Initial delivery plan

### Phase P0 — Protocol evidence — MUST COME FIRST

- install/confirm PEC Windows Service;
- run official/known-good tester;
- perform a small permitted real transaction;
- capture exact Request/Response evidence;
- capture representative failure behavior;
- complete `PEC-POC-PROTOCOL.md`.

**P1 implementation is blocked until P0 provides enough evidence to define the PEC adapter boundary safely.**

### Phase P1 — Deterministic local core

- WinForms UI;
- single Toman → IRR boundary;
- dual-unit pre-dispatch confirmation;
- financial calculation;
- SQLite schema/migrations;
- WAL + FULL durability verification;
- named process mutex;
- state machine;
- event log;
- history;
- Needs Review;
- clipboard export;
- fake/test PEC adapter;
- daily backup and restore workflow.

### Phase P2 — PEC integration

- verified PEC adapter;
- single-flight terminal lock;
- startup recovery;
- late-response reconciliation;
- raw evidence capture;
- provider amount verification if available;
- real-device acceptance matrix.

### Phase P3 — SRWF handoff

- paste parser on SRWF side;
- `attempt_id` duplicate rejection;
- Entry Ref match enforcement;
- audited Entry Ref correction route;
- financial snapshot/display/print integration.

Future possibilities, explicitly outside the initial scope:

- direct SRWF-to-agent orchestration;
- multi-provider POS adapters;
- online Sayad inquiry;
- automated reconciliation through official PSP inquiry APIs.

## 25. Definition of done for the first production-capable desktop version

The desktop utility is production-capable only when all of the following are true:

- PEC POC is complete and documented.
- Exact real-device request/response contract is tested.
- Monetary unit is verified end-to-end.
- UI → Core Toman/IRR conversion is tested as a single boundary.
- Pre-dispatch confirmation shows the exact amount in both IRR and Toman.
- Database is initialized and migrated deterministically.
- Required SQLite `WAL + FULL` durability settings are established and verified on startup.
- Named process mutex prevents concurrent operational instances.
- Partial unique database index prevents multiple terminal-blocking attempts.
- All payment attempts are persisted before possible dispatch.
- Raw provider evidence is retained.
- Single-flight protection works.
- Startup recovery detects unresolved/stale states.
- UNKNOWN never permits normal retry.
- Late responses can be captured/reconciled while the unresolved terminal remains blocked.
- Provider success and business verification are represented separately.
- Provider amount mismatch, when amount is available, forces review without falsifying provider success.
- Entry Ref correction is append-only audited.
- No post-dispatch hard-delete exists.
- History and Needs Review are usable.
- Clipboard copy is verified and retry-safe.
- `entry_ref`, `payment_id`, and `attempt_id` are exported.
- Application-observed and provider-reported timestamps are not conflated.
- Real-device success and failure acceptance tests pass.
- App restart/crash recovery scenarios pass.
- Automated backup runs to an independent destination.
- At least one restore test has succeeded and is documented.
- No sensitive card authentication data is stored.
- Build/package process is reproducible.
- README and operator instructions reflect actual tested behavior, not assumptions.

## 26. Source register

This baseline is informed by:

- PEC / تجارت الکترونیک پارسیان PC-POS connection guide distributed by Mabna System Asan.
- Microsoft .NET lifecycle documentation confirming .NET 10 LTS support through November 2028.
- Microsoft .NET documentation for named system mutexes and cross-process synchronization.
- Microsoft.Data.Sqlite documentation for transactions and `BackupDatabase()`.
- SQLite official documentation for partial unique indexes.
- SQLite official WAL and `PRAGMA synchronous` durability documentation.
- SQLite official atomic-commit / reliability documentation.
- A public `rahkar-pcpos` PEC implementation used only as non-authoritative behavioral evidence.

Where official PEC documentation and public implementation differ, or where official documentation is silent, the production specification must remain unresolved until the real-device POC provides evidence.

---

## 27. Versioning and closure rule

This `0.1.1-draft` is a pre-POC governance/design baseline.

It intentionally contains unresolved PEC protocol fields.

The next semantic design baseline SHALL be:

```text
0.2.0
```

and SHALL NOT be published until:

```text
docs/PEC-POC-PROTOCOL.md
```

contains real-device evidence sufficient to close or revise the PEC protocol assumptions.

The purpose of `0.1.1` is to prevent implementation from silently turning assumptions into facts before P0.
