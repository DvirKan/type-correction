namespace TypeCorrection;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\TypeCorrection.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("התוכנה כבר פועלת – חפש את האייקון ליד השעון.", "תיקון שפה",
                MessageBoxButtons.OK, MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1, MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
            return;
        }

        DebugLog.Enabled = args.Contains("--debug");
        DebugLog.Write("---- start ----");

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(args.Contains("--startup")));
    }
}
