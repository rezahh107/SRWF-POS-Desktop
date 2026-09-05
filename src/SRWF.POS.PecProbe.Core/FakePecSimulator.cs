namespace SRWF.POS.PecProbe.Core;

public sealed class FakePecSandbox : IDisposable
{
    public string Root { get; }
    public string RequestDirectory { get; }
    public string ResponseDirectory { get; }
    private readonly string _marker;

    private FakePecSandbox(string root)
    {
        Root = root;
        RequestDirectory = Path.Combine(root, "Request");
        ResponseDirectory = Path.Combine(root, "Response");
        Directory.CreateDirectory(RequestDirectory);
        Directory.CreateDirectory(ResponseDirectory);
        _marker = Path.Combine(root, ".srwf-pec-simulator");
        File.WriteAllText(_marker, "FAKE PEC SANDBOX ONLY");
    }

    public static FakePecSandbox Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "SRWF-POS-PEC-Simulator", Guid.NewGuid().ToString("N"));
        return new(root);
    }

    internal void AssertSafe()
    {
        if (!File.Exists(_marker)) throw new InvalidOperationException("Fake PEC simulator marker is missing.");
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!Path.GetFullPath(Root).StartsWith(temp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fake PEC simulator refuses to run outside the OS temporary directory.");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
    }
}

public sealed class FakePecSimulator(FakePecSandbox sandbox)
{
    private static readonly byte[] RequestBytes = System.Text.Encoding.ASCII.GetBytes("Amount=1000\r\ntype=1\r\nIP=127.0.0.1\r\nport=1234\r\n");

    public async Task RunAsync(FakePecScenario scenario, CancellationToken cancellationToken = default)
    {
        sandbox.AssertSafe();
        var request = Path.Combine(sandbox.RequestDirectory, "TransAction.txt");
        var response = Path.Combine(sandbox.ResponseDirectory, "TransAction.txt");

        switch (scenario)
        {
            case FakePecScenario.ExternalTesterCreatesRequestThenServiceConsumes:
                await File.WriteAllBytesAsync(request, RequestBytes, cancellationToken);
                await Task.Delay(80, cancellationToken);
                File.Delete(request);
                break;
            case FakePecScenario.DirectRequestWrite:
                await File.WriteAllBytesAsync(request, RequestBytes, cancellationToken);
                break;
            case FakePecScenario.TempFileThenRenameRequest:
                var temp = Path.Combine(sandbox.RequestDirectory, "tx.tmp");
                await File.WriteAllBytesAsync(temp, RequestBytes, cancellationToken);
                File.Move(temp, request);
                break;
            case FakePecScenario.RequestDisappearsTooQuicklyToCapture:
                await File.WriteAllBytesAsync(request, RequestBytes, cancellationToken);
                File.Delete(request);
                break;
            case FakePecScenario.SuccessfulLookingResponse:
                await File.WriteAllTextAsync(response, "RESULT CODE 00\r\n", cancellationToken);
                break;
            case FakePecScenario.CancelledLookingResponse:
                await File.WriteAllTextAsync(response, "RESULT CODE 99\r\n", cancellationToken);
                break;
            case FakePecScenario.MalformedResponse:
                await File.WriteAllTextAsync(response, "MALFORMED", cancellationToken);
                break;
            case FakePecScenario.RequestNeverConsumed:
                await File.WriteAllBytesAsync(request, RequestBytes, cancellationToken);
                break;
            case FakePecScenario.RequestConsumedButResponseNeverAppears:
                await File.WriteAllBytesAsync(request, RequestBytes, cancellationToken);
                await Task.Delay(50, cancellationToken);
                File.Delete(request);
                break;
            case FakePecScenario.DelayedResponse:
                await File.WriteAllBytesAsync(request, RequestBytes, cancellationToken);
                await Task.Delay(50, cancellationToken);
                File.Delete(request);
                await Task.Delay(250, cancellationToken);
                await File.WriteAllTextAsync(response, "RESULT CODE 00\r\n", cancellationToken);
                break;
            case FakePecScenario.StaleResponseAlreadyPresent:
                await File.WriteAllTextAsync(response, "STALE CODE 00\r\n", cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }
}
