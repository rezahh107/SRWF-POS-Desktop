using System.Collections.Concurrent;
using SRWF.POS.PecProbe.Core;

namespace SRWF.POS.PecProbe.App;

internal sealed class WindowsDirectoryObserver : IAsyncDisposable
{
    private readonly ProbeConfiguration _config;
    private readonly DiagnosticCollector _collector;
    private readonly OperatorObservationInput _operatorInput;
    private readonly ConcurrentDictionary<string, (long Length, DateTime LastWriteUtc)> _pollState = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastCapturedHash = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private readonly bool _recordRequestAsObservedTesterEvidence;

    public event Action<ArtifactCapture>? ArtifactCaptured;
    public bool IsRunning => _cts is not null;

    public WindowsDirectoryObserver(ProbeConfiguration config, DiagnosticCollector collector, OperatorObservationInput operatorInput, bool recordRequestAsObservedTesterEvidence)
    {
        _config = config;
        _collector = collector;
        _operatorInput = operatorInput;
        _recordRequestAsObservedTesterEvidence = recordRequestAsObservedTesterEvidence;
    }

    public Task StartAsync()
    {
        if (IsRunning) return Task.CompletedTask;
        if (!Directory.Exists(_config.RequestDirectory) || !Directory.Exists(_config.ResponseDirectory))
            throw new DirectoryNotFoundException("Request and Response directories must exist. Probe never creates PEC directories.");

        SeedPollState("Request", _config.RequestDirectory);
        SeedPollState("Response", _config.ResponseDirectory);
        _cts = new CancellationTokenSource();
        AddWatcher("Request", _config.RequestDirectory);
        AddWatcher("Response", _config.ResponseDirectory);
        _pollTask = PollLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        if (_pollTask is not null)
        {
            try { await _pollTask; } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _cts = null;
        _pollTask = null;
    }

    private void AddWatcher(string role, string directory)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            Filter = "*",
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        watcher.Created += (_, e) => _ = OnEventAsync(role, "Created", e.FullPath, null);
        watcher.Changed += (_, e) => _ = OnEventAsync(role, "Changed", e.FullPath, null);
        watcher.Deleted += (_, e) => _collector.RecordEvent(role, "Deleted", e.FullPath,
            status: role == "Request" ? "REQUEST_DISAPPEARED" : "RESPONSE_DISAPPEARED");
        watcher.Renamed += (_, e) => _ = OnEventAsync(role, "Renamed", e.FullPath, e.OldFullPath);
        watcher.Error += (_, e) => _collector.RecordError("FILESYSTEM_WATCHER_ERROR", e.GetException());
        _watchers.Add(watcher);
    }

    private void SeedPollState(string role, string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var info = new FileInfo(path);
            _pollState[Key(role, path)] = (info.Length, info.LastWriteTimeUtc);
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Clamp(_config.PollIntervalMilliseconds, 20, 1000)));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await PollDirectoryAsync("Request", _config.RequestDirectory, cancellationToken);
            await PollDirectoryAsync("Response", _config.ResponseDirectory, cancellationToken);
        }
    }

    private async Task PollDirectoryAsync(string role, string directory, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = Key(role, path);
                seen.Add(key);
                var info = new FileInfo(path);
                var current = (info.Length, info.LastWriteTimeUtc);
                if (!_pollState.TryGetValue(key, out var previous))
                {
                    _pollState[key] = current;
                    await OnEventAsync(role, "PollCreated", path, null);
                }
                else if (previous != current)
                {
                    _pollState[key] = current;
                    await OnEventAsync(role, "PollChanged", path, null);
                }
            }

            foreach (var key in _pollState.Keys.Where(k => k.StartsWith(role + "|", StringComparison.Ordinal) && !seen.Contains(k)).ToArray())
            {
                if (_pollState.TryRemove(key, out _))
                {
                    var path = key[(role.Length + 1)..];
                    _collector.RecordEvent(role, "PollDeleted", path,
                        status: role == "Request" ? "REQUEST_DISAPPEARED" : "RESPONSE_DISAPPEARED");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            _collector.RecordError("POLL_ERROR", ex);
        }
    }

    private async Task OnEventAsync(string role, string eventType, string path, string? oldPath)
    {
        _collector.RecordEvent(role, eventType, path, oldPath,
            status: role == "Request" ? "REQUEST_EVENT_OBSERVED" : "RESPONSE_EVENT_OBSERVED");

        if (eventType.Contains("Deleted", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var capture = await FileEvidence.TryCaptureAsync(role, path, cancellationToken: _cts?.Token ?? CancellationToken.None);
            if (capture is null)
            {
                if (role == "Request") _collector.RecordRequestMiss(path);
                else _collector.RecordEvent(role, "Capture", path, status: "RESPONSE_CONTENT_NOT_CAPTURED");
                return;
            }

            if (_lastCapturedHash.TryGetValue(path, out var previousHash) && string.Equals(previousHash, capture.Sha256, StringComparison.OrdinalIgnoreCase))
                return;
            _lastCapturedHash[path] = capture.Sha256;

            _collector.RecordEvent(role, "Capture", path, length: capture.RawBytes.Length, sha: capture.Sha256,
                status: role == "Request" ? "REQUEST_CONTENT_CAPTURED" : "RESPONSE_CONTENT_CAPTURED");

            if (role == "Request")
            {
                if (_recordRequestAsObservedTesterEvidence) _collector.RecordRequestCapture(capture, _operatorInput);
            }
            else _collector.RecordResponseCapture(capture);
            ArtifactCaptured?.Invoke(capture);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _collector.RecordError("ARTIFACT_CAPTURE_ERROR", ex);
        }
    }

    private static string Key(string role, string path) => role + "|" + Path.GetFullPath(path);

    public async ValueTask DisposeAsync() => await StopAsync();
}
