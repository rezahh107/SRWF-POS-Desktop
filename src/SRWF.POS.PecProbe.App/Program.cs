namespace SRWF.POS.PecProbe.App;

internal static class Program
{
    private const string MutexName = @"Local\SRWF-POS-PEC-Probe";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var acquired);
        if (!acquired)
        {
            MessageBox.Show(
                "Another SRWF-POS-PEC-Probe instance already owns the observation/payment-control mutex.\n\nThis instance will not enter observation or PUBLISH mode.",
                "SRWF-POS-PEC-Probe",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        Application.Run(new MainForm());
        mutex.ReleaseMutex();
    }
}
