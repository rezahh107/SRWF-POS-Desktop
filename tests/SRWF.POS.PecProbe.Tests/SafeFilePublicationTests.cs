using System.Text;
using SRWF.POS.PecProbe.Core;

namespace SRWF.POS.PecProbe.Tests;

public class SafeFilePublicationTests
{
    [Fact]
    public async Task TempThenRenameSuccessMovesExactPreparedBytes()
    {
        using var sandbox = new TempDirectory("srwf-temp-success");
        var temp = Path.Combine(sandbox.Path, "request.tmp");
        var destination = Path.Combine(sandbox.Path, "TransAction.txt");
        var bytes = Encoding.ASCII.GetBytes("Amount=100\r\n");

        await SafeFilePublication.TempThenRenameAsync(temp, destination, bytes);

        Assert.False(File.Exists(temp));
        Assert.True(File.Exists(destination));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task TempFailureNeverDeletesCompetingPathOccupant()
    {
        using var sandbox = new TempDirectory("srwf-temp-nodelete");
        var temp = Path.Combine(sandbox.Path, "request.tmp");
        var destination = Path.Combine(sandbox.Path, "TransAction.txt");
        var probeBytes = Encoding.ASCII.GetBytes("Amount=100\r\n");
        var competitorBytes = Encoding.ASCII.GetBytes("EXTERNAL-EVIDENCE-MUST-SURVIVE");

        var ex = await Assert.ThrowsAsync<IOException>(() => SafeFilePublication.TempThenRenameCoreAsync(
            temp,
            destination,
            probeBytes,
            beforeMove: async (temporaryPath, _, _) =>
            {
                File.Delete(temporaryPath);
                await File.WriteAllBytesAsync(temporaryPath, competitorBytes);
                throw new IOException("deterministic publication-boundary failure");
            }));

        Assert.Contains("deterministic publication-boundary failure", ex.Message);
        Assert.True(File.Exists(temp));
        Assert.Equal(competitorBytes, await File.ReadAllBytesAsync(temp));
        Assert.False(File.Exists(destination));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
