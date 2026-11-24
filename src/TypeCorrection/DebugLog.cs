namespace TypeCorrection;

/// <summary>
/// Diagnostic log, OFF by default. Only when the app is started with --debug does it write
/// to %TEMP%\TypeCorrection-debug.log, and that log DOES contain typed text, so use it only
/// for troubleshooting and delete it afterwards.
/// </summary>
internal static class DebugLog
{
    public static bool Enabled { get; set; }
    public static string FilePath => Path.Combine(Path.GetTempPath(), "TypeCorrection-debug.log");

    public static void Write(string message)
    {
        if (!Enabled) return;
        try { File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
        catch (IOException) { }
    }
}
