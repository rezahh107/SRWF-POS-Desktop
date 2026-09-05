using Microsoft.Win32;
using System.ServiceProcess;

namespace SRWF.POS.PecProbe.App;

internal sealed record PecServiceInfo(string ServiceName, string DisplayName, string Status, string StartType, string? ExecutablePath);

internal static class WindowsServiceInspector
{
    private static readonly string[] Terms = ["PEC", "PCPOS", "PC POS", "PEC PCPOS"];

    public static IReadOnlyList<PecServiceInfo> FindCandidates()
    {
        var results = new List<PecServiceInfo>();
        try
        {
            foreach (var service in ServiceController.GetServices())
            {
                using (service)
                {
                    if (!Terms.Any(term =>
                        service.ServiceName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                        service.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    string startType;
                    try { startType = service.StartType.ToString(); }
                    catch { startType = "UNKNOWN"; }

                    results.Add(new(service.ServiceName, service.DisplayName, SafeStatus(service), startType, TryGetImagePath(service.ServiceName)));
                }
            }
        }
        catch
        {
            // Preflight reports an empty/unknown service set; it never manipulates services.
        }
        return results;
    }

    private static string SafeStatus(ServiceController service)
    {
        try { return service.Status.ToString(); }
        catch { return "UNKNOWN"; }
    }

    private static string? TryGetImagePath(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: false);
            return key?.GetValue("ImagePath")?.ToString();
        }
        catch { return null; }
    }
}
