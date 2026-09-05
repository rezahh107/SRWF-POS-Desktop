using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SRWF.POS.PecProbe.Core;

namespace SRWF.POS.PecProbe.App;

internal sealed class MainForm : Form
{
    private readonly TextBox _baseDir = new() { Width = 520 };
    private readonly TextBox _requestDir = new() { Width = 520 };
    private readonly TextBox _responseDir = new() { Width = 520 };
    private readonly Label _preflight = new() { AutoSize = true, MaximumSize = new Size(900, 0) };
    private readonly RadioButton _observeRadio = new() { Text = "OBSERVE ONLY", Checked = true, AutoSize = true };
    private readonly RadioButton _publishRadio = new() { Text = "PUBLISH", Enabled = false, AutoSize = true };
    private readonly CheckBox _overrideCheck = new() { Text = "UNVERIFIED PUBLISH OVERRIDE", AutoSize = true, ForeColor = Color.DarkRed };
    private readonly ComboBox _overrideStrategy = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly TextBox _testerAmount = new() { Width = 180 };
    private readonly ComboBox _testerUnit = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly Button _startObservation = new() { Text = "Start Observation", AutoSize = true };
    private readonly Button _stopObservation = new() { Text = "Stop Observation", AutoSize = true, Enabled = false };
    private readonly Label _contract = new() { AutoSize = true, MaximumSize = new Size(900, 0) };
    private readonly TextBox _rawAmount = new() { Width = 180 };
    private readonly NumericUpDown _maxRawAmount = new() { Minimum = 1, Maximum = decimal.MaxValue, Value = 100000, Width = 180 };
    private readonly TextBox _typeValue = new() { Width = 180, Enabled = false };
    private readonly TextBox _ipValue = new() { Width = 180, Enabled = false };
    private readonly TextBox _portValue = new() { Width = 180, Enabled = false };
    private readonly NumericUpDown _operationalTimeout = new() { Minimum = 1, Maximum = 3600, Value = 120, Width = 180 };
    private readonly NumericUpDown _lateWindow = new() { Minimum = 0, Maximum = 7200, Value = 300, Width = 180 };
    private readonly Button _startProbe = new() { Text = "Start PEC Probe", AutoSize = true };
    private readonly Label _publishStatus = new() { AutoSize = true, MaximumSize = new Size(900, 0) };
    private readonly Label _diagnosticFolder = new() { AutoSize = true, MaximumSize = new Size(900, 0) };
    private readonly Button _openDiagnostics = new() { Text = "Open Diagnostic Folder", AutoSize = true, Enabled = false };
    private readonly Button _archiveArtifacts = new() { Text = "Acknowledge & Archive Provider Artifacts", AutoSize = true, Enabled = false };
    private readonly Button _exportSafe = new() { Text = "Export Safe Diagnostic Bundle", AutoSize = true, Enabled = false };

    private DiagnosticCollector? _collector;
    private WindowsDirectoryObserver? _observer;
    private ProbeConfiguration? _currentConfig;
    private OperatorObservationInput _currentOperatorInput = new(null, TesterDisplayedUnit.Unknown);
    private IReadOnlyList<DirectorySnapshot> _beforeSnapshots = [];
    private bool _observing;
    private bool _publishing;
    private bool _responseCapturedDuringCurrentPublish;
    private bool _unresolvedPublishedAttempt;
    private long _lastMaxRawAmount = 100000;
    private readonly List<string> _pendingCapChanges = [];

    public MainForm()
    {
        Text = "PEC PROBE — TEST / EVIDENCE MODE";
        Width = 1050;
        Height = 900;
        AutoScroll = true;
        StartPosition = FormStartPosition.CenterScreen;

        var defaults = ProbeConfiguration.CreateDefault(DefaultDiagnosticsRoot());
        _baseDir.Text = defaults.PecBaseDirectory;
        _requestDir.Text = defaults.RequestDirectory;
        _responseDir.Text = defaults.ResponseDirectory;
        _testerUnit.Items.AddRange(Enum.GetNames<TesterDisplayedUnit>());
        _testerUnit.SelectedItem = TesterDisplayedUnit.Unknown.ToString();
        _overrideStrategy.Items.AddRange(new object[] { "DIRECT_WRITE", "TEMP_THEN_RENAME" });
        _overrideStrategy.SelectedIndex = 0;

        Controls.Add(BuildUi());
        WireEvents();
        _ = RefreshPreflightAsync();
    }

    private Control BuildUi()
    {
        var root = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(12)
        };

        root.Controls.Add(new Label
        {
            Text = "PEC PROBE — TEST / EVIDENCE MODE\nOBSERVE → MEASURE → INFER CANDIDATE CONTRACT → REPRODUCE → VALIDATE",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold)
        });

        root.Controls.Add(Group("Environment", Table(
            ("PEC Base Directory", _baseDir),
            ("Request Directory", _requestDir),
            ("Response Directory", _responseDir),
            ("Preflight", Row(new Button { Name = "RefreshPreflight", Text = "Refresh Preflight", AutoSize = true }, _preflight)))));

        root.Controls.Add(Group("Mode", Table(
            ("Default", Row(_observeRadio, _publishRadio)),
            ("Expert", Row(_overrideCheck, _overrideStrategy)))));

        root.Controls.Add(Group("Observation", Table(
            ("Tester Entered Amount", _testerAmount),
            ("Tester Displayed Unit", _testerUnit),
            ("Session", Row(_startObservation, _stopObservation)))));

        root.Controls.Add(Group("Observed Contract", Table(("Evidence", _contract))));

        root.Controls.Add(Group("Publish", Table(
            ("Raw Amount", _rawAmount),
            ("MaxProbeRawAmount", _maxRawAmount),
            ("type (only when supported)", _typeValue),
            ("IP (only when supported)", _ipValue),
            ("port (only when supported)", _portValue),
            ("Operational timeout (s)", _operationalTimeout),
            ("Late-response window (s)", _lateWindow),
            ("Action", _startProbe),
            ("Status", _publishStatus)))));

        root.Controls.Add(Group("Evidence", Table(
            ("Current diagnostic folder", _diagnosticFolder),
            ("Actions", Row(_openDiagnostics, _archiveArtifacts, _exportSafe)))));

        root.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(920, 0),
            ForeColor = Color.DarkRed,
            Text = "Safety: No automatic retry. No automatic provider-file deletion. Timeout = UNKNOWN. Candidate PEC mappings are not official contract facts. Physical PEC validation remains pending."
        });
        return root;
    }

    private void WireEvents()
    {
        var refresh = FindControl<Button>(this, "RefreshPreflight");
        refresh.Click += async (_, _) => await RefreshPreflightAsync();
        _startObservation.Click += async (_, _) => await StartObservationAsync();
        _stopObservation.Click += async (_, _) => await StopObservationAsync();
        _startProbe.Click += async (_, _) => await StartPublishAsync();
        _openDiagnostics.Click += (_, _) => OpenDiagnosticFolder();
        _archiveArtifacts.Click += async (_, _) => await ArchiveProviderArtifactsAsync();
        _exportSafe.Click += (_, _) => ExportSafeBundle();
        _overrideCheck.CheckedChanged += (_, _) =>
        {
            _publishRadio.Enabled = _overrideCheck.Checked || NormalPublishEvidenceReady();
            if (!_publishRadio.Enabled && _publishRadio.Checked) _observeRadio.Checked = true;
            UpdateConnectionFieldAvailability();
        };
        _maxRawAmount.ValueChanged += (_, _) =>
        {
            var now = (long)_maxRawAmount.Value;
            if (now != _lastMaxRawAmount)
            {
                var note = $"{_lastMaxRawAmount} -> {now}";
                if (_collector is not null)
                    _collector.RecordEvent("Configuration", "MaxProbeRawAmountChanged", _collector.SessionDirectory,
                        status: "MAX_PROBE_RAW_AMOUNT_CHANGED", note: note);
                else
                    _pendingCapChanges.Add(note);
                _lastMaxRawAmount = now;
            }
        };
    }

    private async Task RefreshPreflightAsync()
    {
        var config = BuildConfiguration();
        var request = await FileEvidence.SnapshotDirectoryAsync("Request", config.RequestDirectory);
        var response = await FileEvidence.SnapshotDirectoryAsync("Response", config.ResponseDirectory);
        var services = WindowsServiceInspector.FindCandidates();
        var baseExists = Directory.Exists(config.PecBaseDirectory);

        var sb = new StringBuilder();
        sb.AppendLine($"Base exists: {baseExists}");
        sb.AppendLine($"Request: exists={request.Exists}, enumerable={request.Enumerable}, files={request.Files.Count}");
        sb.AppendLine($"Response: exists={response.Exists}, enumerable={response.Enumerable}, files={response.Files.Count}");
        if (services.Count == 0) sb.AppendLine("Candidate PEC/PCPOS service: NOT FOUND (not proof that PEC is absent)");
        foreach (var svc in services)
            sb.AppendLine($"Service: {svc.ServiceName} | {svc.DisplayName} | {svc.Status} | Start={svc.StartType} | Path={svc.ExecutablePath ?? "UNKNOWN"}");
        sb.AppendLine("Preflight is read-only. Probe does not start/stop/restart services and does not create PEC directories.");
        _preflight.Text = sb.ToString();
    }

    private async Task StartObservationAsync()
    {
        if (_observing || _publishing) return;
        if (!_observeRadio.Checked)
        {
            MessageBox.Show("Select OBSERVE ONLY before starting an observation session.");
            return;
        }
        var config = BuildConfiguration();
        if (!Directory.Exists(config.RequestDirectory) || !Directory.Exists(config.ResponseDirectory))
        {
            MessageBox.Show("Request/Response directories are missing. Probe will not create PEC directories. Verify PEC/EasySoft installation/configuration first.");
            return;
        }

        _currentOperatorInput = ReadOperatorObservationInput();
        var sessionId = $"{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}"[..(17 + 1 + 12)];
        var session = new DiagnosticSessionMetadata(
            sessionId,
            DateTimeOffset.UtcNow,
            "IMPLEMENTED_AGAINST_EXTERNAL_EVIDENCE",
            "PENDING_REAL_PEC_VALIDATION",
            ProbeMode.ObserveOnly,
            _currentOperatorInput);

        _collector = new DiagnosticCollector(config, session);
        _currentConfig = config;
        FlushPendingCapChanges();
        _collector.WriteEnvironment(BuildEnvironmentEvidence());
        _collector.WriteServices(WindowsServiceInspector.FindCandidates());
        var requestBefore = await FileEvidence.SnapshotDirectoryAsync("Request", config.RequestDirectory);
        var responseBefore = await FileEvidence.SnapshotDirectoryAsync("Response", config.ResponseDirectory);
        _beforeSnapshots = [requestBefore, responseBefore];
        _collector.WriteDirectoryBefore(_beforeSnapshots);

        _observer = new WindowsDirectoryObserver(config, _collector, _currentOperatorInput, recordRequestAsObservedTesterEvidence: true);
        _observer.ArtifactCaptured += OnArtifactCaptured;
        await _observer.StartAsync();
        _observing = true;
        _startObservation.Enabled = false;
        _stopObservation.Enabled = true;
        _archiveArtifacts.Enabled = false;
        _diagnosticFolder.Text = _collector.SessionDirectory;
        _openDiagnostics.Enabled = true;
        _exportSafe.Enabled = true;
        _contract.Text = "Observation armed. Run the known-good external PEC Tester now. Probe is not writing to PEC directories.";
    }

    private async Task StopObservationAsync()
    {
        if (!_observing || _collector is null || _currentConfig is null) return;
        if (_observer is not null) await _observer.StopAsync();
        _observing = false;
        _startObservation.Enabled = true;
        _stopObservation.Enabled = false;

        if (_collector.RequestContract is not null)
        {
            var existedBefore = _beforeSnapshots.SelectMany(x => x.Files).Any(f =>
                string.Equals(Path.GetFullPath(f.FullPath), Path.GetFullPath(_collector.RequestContract.DestinationPath), StringComparison.OrdinalIgnoreCase));
            _collector.FinalizePublicationPattern(existedBefore);
        }

        var requestAfter = await FileEvidence.SnapshotDirectoryAsync("Request", _currentConfig.RequestDirectory);
        var responseAfter = await FileEvidence.SnapshotDirectoryAsync("Response", _currentConfig.ResponseDirectory);
        _collector.WriteDirectoryAfter(new[] { requestAfter, responseAfter });
        _collector.Complete("OBSERVATION_STOPPED");
        _archiveArtifacts.Enabled = true;
        UpdateContractUi();
        _publishRadio.Enabled = NormalPublishEvidenceReady() || _overrideCheck.Checked;
    }

    private void OnArtifactCaptured(ArtifactCapture capture)
    {
        if (capture.DirectoryRole == "Response" && _publishing)
            _responseCapturedDuringCurrentPublish = true;

        if (IsDisposed) return;
        BeginInvoke(new Action(() =>
        {
            UpdateContractUi();
            if (capture.DirectoryRole == "Response" && _collector?.ResponseParsing is not null)
                _publishStatus.Text = $"Response captured before interpretation. Candidate parser: {_collector.ResponseParsing.Label} ({_collector.ResponseParsing.EvidenceLevel}).";
        }));
    }

    private async Task StartPublishAsync()
    {
        if (_observing || _publishing) return;
        if (!_publishRadio.Checked)
        {
            MessageBox.Show("Select PUBLISH mode explicitly. OBSERVE ONLY is the default and never dispatches a Request.");
            return;
        }
        if (_unresolvedPublishedAttempt)
        {
            MessageBox.Show("A prior PUBLISH attempt in this process remains unresolved/candidate-only. No second Request will be sent automatically or as a normal retry.");
            return;
        }
        if (!long.TryParse(_rawAmount.Text.Trim(), out var rawAmount))
        {
            MessageBox.Show("Enter Raw Amount as an integer. No Rial/Toman conversion is performed.");
            return;
        }

        var config = BuildConfiguration();
        var expertOverride = _overrideCheck.Checked;
        if (!expertOverride && (_collector?.RequestContract is null || !NormalPublishEvidenceReady()))
        {
            MessageBox.Show("PUBLISH is locked: observed Tester evidence is insufficient. Start with OBSERVE ONLY.");
            return;
        }

        if (_collector is null)
            await CreateOverrideDiagnosticSessionAsync(config);
        if (_collector is null) return;

        var requestSnapshot = await FileEvidence.SnapshotDirectoryAsync("Request", config.RequestDirectory);
        var responseSnapshot = await FileEvidence.SnapshotDirectoryAsync("Response", config.ResponseDirectory);
        var staleRequest = requestSnapshot.Files.Count > 0;
        var staleResponse = responseSnapshot.Files.Count > 0;
        if (_unresolvedPublishedAttempt) staleRequest = true;

        var unresolvedUnitConfirmed = MessageBox.Show(
            "PEC amount unit is still NOT PROVEN.\n\nThe value below will be published exactly as RAW units with NO Rial/Toman conversion:\n\n" + rawAmount +
            "\n\nContinue with this controlled POC publish?",
            "Unresolved amount unit",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning) == DialogResult.Yes;

        var expertStrategy = expertOverride ? ReadOverrideStrategy() : (PublicationStrategy?)null;
        var amountObserved = _collector.AmountAnalysis?.Status == "AMOUNT_RELATION_OBSERVED";
        var gate = PublishGate.Evaluate(new(
            _collector.RequestContract,
            rawAmount,
            (long)_maxRawAmount.Value,
            staleRequest,
            staleResponse,
            amountObserved,
            AmountUnitStillUnresolved: true,
            AdditionalUnresolvedUnitConfirmation: unresolvedUnitConfirmed,
            ExpertOverride: expertOverride,
            ExpertOverrideStrategy: expertStrategy));

        if (!gate.Allowed)
        {
            _publishStatus.Text = gate.Status + ": " + string.Join(", ", gate.Reasons);
            MessageBox.Show(_publishStatus.Text);
            return;
        }

        var contract = _collector.RequestContract;
        PublicationStrategy strategy;
        string destinationPath;
        byte[] prepared;
        RequestComparison comparison;

        if (expertOverride)
        {
            strategy = expertStrategy!.Value;
            _collector.RecordPublishOverride(strategy);
        }
        else
        {
            strategy = contract!.PublicationPattern switch
            {
                PublicationPattern.DirectCreateAndWrite => PublicationStrategy.DirectWrite,
                PublicationPattern.TempFileThenRename => PublicationStrategy.TempThenRename,
                _ => throw new InvalidOperationException("Normal publish cannot select an unobserved publication strategy.")
            };
        }

        if (contract is not null && contract.TextEvidence.DecodingSafe && contract.AmountFieldName is not null)
        {
            var overrides = SupportedConnectionOverrides(contract);
            var render = RequestTemplateRenderer.Render(contract, rawAmount, overrides);
            prepared = render.Bytes;
            destinationPath = contract.DestinationPath;
            comparison = RequestComparer.Compare(contract.ObservedRawBytes, prepared);
        }
        else
        {
            if (!expertOverride) throw new InvalidOperationException("Observed contract is not safely reproducible.");
            if (string.IsNullOrWhiteSpace(_typeValue.Text) || string.IsNullOrWhiteSpace(_ipValue.Text) || string.IsNullOrWhiteSpace(_portValue.Text))
            {
                MessageBox.Show("UNVERIFIED PUBLISH OVERRIDE using the external candidate shape requires explicit type, IP, and port values. Probe will not invent them.");
                return;
            }
            prepared = BuildUnverifiedExternalCandidate(rawAmount);
            destinationPath = Path.Combine(config.RequestDirectory, "TransAction.txt");
            comparison = new("NOT_COMPARABLE", false, false, false, false, false, false,
                "MISSING_OBSERVED_REQUEST_BYTES", Hashing.Sha256(prepared),
                ["Generated only under explicit UNVERIFIED PUBLISH OVERRIDE using external candidate evidence."]);
        }

        if (!Path.GetFullPath(destinationPath).StartsWith(Path.GetFullPath(config.RequestDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Blocked: destination path is outside the configured Request directory.");
            return;
        }

        _collector.WriteJson("prepublish-directory-snapshot.json", new { requestSnapshot, responseSnapshot, capturedAtUtc = DateTimeOffset.UtcNow });
        _collector.WriteJson("publish-attempt.json", new
        {
            attemptId = Guid.NewGuid().ToString("N"),
            persistedBeforeDispatch = true,
            startedAtUtc = DateTimeOffset.UtcNow,
            rawAmount,
            maxProbeRawAmount = (long)_maxRawAmount.Value,
            amountUnit = "UNRESOLVED_RAW_UNITS",
            automaticRetry = false,
            expertOverride
        });
        _collector.RecordPublishedRequest(prepared, strategy,
            expertOverride ? "Explicit UNVERIFIED PUBLISH OVERRIDE" : contract!.PublicationStrategyEvidence,
            expertOverride ? "UNVERIFIED" : contract!.PublicationStrategyConfidence,
            rawAmount,
            comparison);

        _currentConfig = config;
        _responseCapturedDuringCurrentPublish = false;
        _publishing = true;
        _startProbe.Enabled = false;
        _archiveArtifacts.Enabled = false;

        _observer = new WindowsDirectoryObserver(config, _collector, _currentOperatorInput, recordRequestAsObservedTesterEvidence: false);
        _observer.ArtifactCaptured += OnArtifactCaptured;
        await _observer.StartAsync();

        try
        {
            _collector.RecordEvent("Publish", "Dispatch", destinationPath, status: "DISPATCH_STARTED");
            await PecRequestPublisher.PublishAsync(
                destinationPath,
                prepared,
                strategy,
                contract?.ObservedTemporaryFileName,
                unverifiedOverride: expertOverride);
            _collector.RecordEvent("Publish", "Dispatch", destinationPath, length: prepared.Length, sha: Hashing.Sha256(prepared), status: "REQUEST_FILE_PUBLISHED");
            _publishStatus.Text = "AWAITING_RESULT — no automatic retry.";

            var operationalDeadline = DateTime.UtcNow.AddSeconds(config.OperationalTimeoutSeconds);
            while (DateTime.UtcNow < operationalDeadline && !_responseCapturedDuringCurrentPublish)
                await Task.Delay(100);

            if (!_responseCapturedDuringCurrentPublish)
            {
                _unresolvedPublishedAttempt = true;
                _collector.RecordEvent("Publish", "Timeout", destinationPath, status: "UNKNOWN", note: "Probe operational timeout expired. This is not a PEC failure determination.");
                _publishStatus.Text = "UNKNOWN — operational timeout expired. Late-response observation continues; no automatic retry.";

                var lateDeadline = DateTime.UtcNow.AddSeconds(config.LateResponseWindowSeconds);
                while (DateTime.UtcNow < lateDeadline && !_responseCapturedDuringCurrentPublish)
                    await Task.Delay(250);

                if (_responseCapturedDuringCurrentPublish)
                {
                    _collector.RecordEvent("Publish", "LateResponse", config.ResponseDirectory, status: "LATE_RESPONSE_OBSERVED", note: "Candidate response captured after UNKNOWN timeout; human/evidence review remains required.");
                    _publishStatus.Text = "UNKNOWN + LATE_RESPONSE_OBSERVED — candidate evidence captured; review required.";
                }
                else
                {
                    _publishStatus.Text = "UNKNOWN — no Response captured within the configured late-response window.";
                }
            }
            else
            {
                _unresolvedPublishedAttempt = true; // Candidate mapping is not yet authoritative PEC contract evidence.
                var candidate = _collector.ResponseParsing?.Label ?? "Response captured; parser evidence unavailable";
                _publishStatus.Text = $"Response captured before interpretation. {candidate}. REAL_PEC_VALIDATION_PENDING.";
            }
        }
        catch (Exception ex)
        {
            _unresolvedPublishedAttempt = true;
            _collector.RecordError("PUBLISH_DISPATCH_ERROR", ex);
            _publishStatus.Text = "RECOVERY_REQUIRED / UNKNOWN: " + ex.Message;
        }
        finally
        {
            if (_observer is not null) await _observer.StopAsync();
            _publishing = false;
            _startProbe.Enabled = true;
            _archiveArtifacts.Enabled = true;
            var requestAfter = await FileEvidence.SnapshotDirectoryAsync("Request", config.RequestDirectory);
            var responseAfter = await FileEvidence.SnapshotDirectoryAsync("Response", config.ResponseDirectory);
            _collector.WriteDirectoryAfter(new[] { requestAfter, responseAfter });
            _collector.Complete(_unresolvedPublishedAttempt ? "PUBLISH_EVIDENCE_REQUIRES_REVIEW" : "PUBLISH_COMPLETED");
        }
    }

    private async Task CreateOverrideDiagnosticSessionAsync(ProbeConfiguration config)
    {
        if (!Directory.Exists(config.RequestDirectory) || !Directory.Exists(config.ResponseDirectory))
        {
            MessageBox.Show("Request/Response directories are missing. Probe will not create them.");
            return;
        }
        _currentOperatorInput = ReadOperatorObservationInput();
        var sessionId = $"{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}"[..30];
        _collector = new DiagnosticCollector(config, new(
            sessionId, DateTimeOffset.UtcNow,
            "IMPLEMENTED_AGAINST_EXTERNAL_EVIDENCE",
            "PENDING_REAL_PEC_VALIDATION",
            ProbeMode.Publish,
            _currentOperatorInput));
        _currentConfig = config;
        FlushPendingCapChanges();
        _collector.WriteEnvironment(BuildEnvironmentEvidence());
        _collector.WriteServices(WindowsServiceInspector.FindCandidates());
        var req = await FileEvidence.SnapshotDirectoryAsync("Request", config.RequestDirectory);
        var res = await FileEvidence.SnapshotDirectoryAsync("Response", config.ResponseDirectory);
        _beforeSnapshots = [req, res];
        _collector.WriteDirectoryBefore(_beforeSnapshots);
        _diagnosticFolder.Text = _collector.SessionDirectory;
        _openDiagnostics.Enabled = _exportSafe.Enabled = _archiveArtifacts.Enabled = true;
    }

    private async Task ArchiveProviderArtifactsAsync()
    {
        if (_collector is null || _currentConfig is null || _observing || _publishing)
        {
            MessageBox.Show("Archive is available only after the active observation/attempt has stopped and a diagnostic session exists.");
            return;
        }

        var files = new List<string>();
        if (Directory.Exists(_currentConfig.RequestDirectory)) files.AddRange(Directory.EnumerateFiles(_currentConfig.RequestDirectory));
        if (Directory.Exists(_currentConfig.ResponseDirectory)) files.AddRange(Directory.EnumerateFiles(_currentConfig.ResponseDirectory));
        if (files.Count == 0)
        {
            MessageBox.Show("No provider artifacts are currently present.");
            return;
        }

        if (MessageBox.Show(
                "This explicit operation will first preserve/hash each provider artifact, re-read and verify it is unchanged, then MOVE it into the current diagnostic session archive.\n\nIt will never force-delete a locked/changed file. Continue?",
                "Acknowledge provider artifacts",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        var archiveDir = Path.Combine(_collector.SessionDirectory, "provider-archive");
        var results = new List<ArchiveOperationResult>();
        foreach (var file in files)
            results.Add(await ProviderArtifactArchiver.ArchiveAsync(file, archiveDir, _collector));
        _collector.WriteJson("provider-archive-results.json", results);
        _collector.Complete("PROVIDER_ARCHIVE_OPERATION_COMPLETED");
        MessageBox.Show(string.Join(Environment.NewLine, results.Select(r => $"{Path.GetFileName(r.SourcePath)}: {r.Status}")));
    }

    private void ExportSafeBundle()
    {
        if (_collector is null) return;
        try
        {
            _collector.Complete(_collector.Session.Status);
            var zip = DiagnosticExporter.ExportSafeBundle(_collector.SessionDirectory);
            MessageBox.Show("Safe Diagnostic Bundle created:\n" + zip + "\n\nRaw Response is excluded by default.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Safe export failed: " + ex.Message);
        }
    }

    private void OpenDiagnosticFolder()
    {
        if (_collector is null || !Directory.Exists(_collector.SessionDirectory)) return;
        Process.Start(new ProcessStartInfo { FileName = _collector.SessionDirectory, UseShellExecute = true });
    }

    private void UpdateContractUi()
    {
        var c = _collector?.RequestContract;
        var a = _collector?.AmountAnalysis;
        if (c is null)
        {
            _contract.Text = "Request content not captured yet. REQUEST_CONTENT_NOT_CAPTURED is a valid evidence outcome when a transient file is missed.";
            return;
        }

        _contract.Text =
            $"Request filename: {c.FileName}\n" +
            $"Encoding: {c.TextEvidence.ProbableEncoding}; BOM={c.TextEvidence.BomPresent}; newline={c.TextEvidence.NewlineRepresentation}; final newline={c.TextEvidence.FinalNewlinePresent}\n" +
            $"Field order: {string.Join(" → ", c.TextEvidence.FieldNames)}\n" +
            $"Candidate schema: {c.CandidateSchemaMatch}\n" +
            $"Publication pattern: {c.PublicationPattern}; temp name={c.ObservedTemporaryFileName ?? "UNKNOWN"}; confidence={c.PublicationStrategyConfidence}\n" +
            $"Observed Tester input: {a?.TesterEnteredAmount?.ToString() ?? "UNKNOWN"} {a?.TesterDisplayedUnit}\n" +
            $"Observed Request amount: {a?.ObservedRequestAmount?.ToString() ?? "UNKNOWN"}\n" +
            $"Candidate relationship: {a?.Relationship}; confidence={a?.Confidence}; {a?.ProofStatus ?? "AMOUNT_UNIT_NOT_PROVEN"}";

        UpdateConnectionFieldAvailability();
    }

    private bool NormalPublishEvidenceReady()
    {
        var c = _collector?.RequestContract;
        return c is not null && c.HasReproducibleStructure && _collector?.AmountAnalysis?.Status == "AMOUNT_RELATION_OBSERVED";
    }

    private IReadOnlyDictionary<string, string> SupportedConnectionOverrides(ObservedRequestContract contract)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = contract.TextEvidence.FieldNames.ToHashSet(StringComparer.Ordinal);
        if (names.Contains("type") && !string.IsNullOrWhiteSpace(_typeValue.Text)) values["type"] = _typeValue.Text.Trim();
        if (names.Contains("IP") && !string.IsNullOrWhiteSpace(_ipValue.Text)) values["IP"] = _ipValue.Text.Trim();
        if (names.Contains("port") && !string.IsNullOrWhiteSpace(_portValue.Text)) values["port"] = _portValue.Text.Trim();
        return values;
    }

    private byte[] BuildUnverifiedExternalCandidate(long rawAmount)
    {
        var type = _typeValue.Text.Trim();
        var ip = _ipValue.Text.Trim();
        var port = _portValue.Text.Trim();
        var text = $"Amount={rawAmount}\r\ntype={type}\r\nIP={ip}\r\nport={port}\r\n";
        return new UTF8Encoding(false).GetBytes(text);
    }

    private void UpdateConnectionFieldAvailability()
    {
        if (_overrideCheck.Checked)
        {
            _typeValue.Enabled = true;
            _ipValue.Enabled = true;
            _portValue.Enabled = true;
            return;
        }

        var names = _collector?.RequestContract?.TextEvidence.FieldNames.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        _typeValue.Enabled = names.Contains("type");
        _ipValue.Enabled = names.Contains("IP");
        _portValue.Enabled = names.Contains("port");
    }

    private void FlushPendingCapChanges()
    {
        if (_collector is null) return;
        foreach (var note in _pendingCapChanges)
            _collector.RecordEvent("Configuration", "MaxProbeRawAmountChanged", _collector.SessionDirectory,
                status: "MAX_PROBE_RAW_AMOUNT_CHANGED", note: note + " (changed before session start)");
        _pendingCapChanges.Clear();
    }

    private PublicationStrategy ReadOverrideStrategy() => _overrideStrategy.SelectedItem?.ToString() switch
    {
        "TEMP_THEN_RENAME" => PublicationStrategy.TempThenRename,
        _ => PublicationStrategy.DirectWrite
    };

    private OperatorObservationInput ReadOperatorObservationInput()
    {
        long? amount = long.TryParse(_testerAmount.Text.Trim(), out var value) ? value : null;
        var unit = Enum.TryParse<TesterDisplayedUnit>(_testerUnit.SelectedItem?.ToString(), out var parsed) ? parsed : TesterDisplayedUnit.Unknown;
        return new(amount, unit);
    }

    private ProbeConfiguration BuildConfiguration() => new(
        _baseDir.Text.Trim(),
        _requestDir.Text.Trim(),
        _responseDir.Text.Trim(),
        DefaultDiagnosticsRoot(),
        PollIntervalMilliseconds: 50,
        OperationalTimeoutSeconds: (int)_operationalTimeout.Value,
        LateResponseWindowSeconds: (int)_lateWindow.Value,
        MaxProbeRawAmount: (long)_maxRawAmount.Value);

    private static object BuildEnvironmentEvidence() => new
    {
        capturedAtUtc = DateTimeOffset.UtcNow,
        machineName = Environment.MachineName,
        windowsIdentity = Environment.UserDomainName + "\\" + Environment.UserName,
        os = RuntimeInformation.OSDescription,
        framework = RuntimeInformation.FrameworkDescription,
        processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        is64BitProcess = Environment.Is64BitProcess
    };

    private static string DefaultDiagnosticsRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SRWF-POS-PEC-Probe",
        "Diagnostics");

    private static GroupBox Group(string title, Control content)
    {
        var box = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10), Width = 960 };
        content.Dock = DockStyle.Fill;
        box.Controls.Add(content);
        return box;
    }

    private static TableLayoutPanel Table(params (string Label, Control Control)[] rows)
    {
        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = rows.Length, Width = 920 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (label, control) in rows)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
            table.Controls.Add(control);
        }
        return table;
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private static T FindControl<T>(Control root, string name) where T : Control
    {
        foreach (Control control in root.Controls)
        {
            if (control is T typed && control.Name == name) return typed;
            var nested = FindControlOrNull<T>(control, name);
            if (nested is not null) return nested;
        }
        throw new InvalidOperationException($"Control {name} not found.");
    }

    private static T? FindControlOrNull<T>(Control root, string name) where T : Control
    {
        foreach (Control control in root.Controls)
        {
            if (control is T typed && control.Name == name) return typed;
            var nested = FindControlOrNull<T>(control, name);
            if (nested is not null) return nested;
        }
        return null;
    }
}
