using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace TypeCorrection;

internal enum Verdict { Valid, Invalid, Unknown }

/// <summary>
/// Uses the spell checker built into Windows (the one Edge/Mail use). It has a
/// dictionary for each installed language, so nothing is bundled with the app.
///
/// All spell-checker COM calls run on a dedicated worker thread: Windows refuses them
/// from inside a keyboard hook (RPC_E_CANTCALLOUT_ININPUTSYNCCALL), so the hook hands
/// the word to the worker and waits briefly (non-pumping wait, no re-entrancy).
/// </summary>
internal sealed class Spelling
{
    private ISpellCheckerFactory? _factory;
    private readonly Dictionary<ushort, ISpellChecker?> _checkers = new();
    private List<string>? _supported;

    private readonly Thread _worker;
    private readonly BlockingCollection<Action> _queue = new();
    private const uint TimeoutMs = 400;

    public Spelling()
    {
        _worker = new Thread(() => { foreach (var work in _queue.GetConsumingEnumerable()) work(); })
        {
            IsBackground = true,
            Name = "Spelling",
        };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();

        OnWorker(() =>
        {
            try
            {
                var type = Type.GetTypeFromCLSID(new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC"));
                _factory = type == null ? null : (ISpellCheckerFactory?)Activator.CreateInstance(type);
            }
            catch (COMException) { _factory = null; }
            return true;
        }, false);
    }

    public bool HasDictionary(Layout layout) => OnWorker(() => For(layout) != null, false);

    public Verdict Check(Layout layout, string word) => OnWorker(() => CheckCore(layout, word), Verdict.Unknown);

    private T OnWorker<T>(Func<T> func, T fallback)
    {
        if (Thread.CurrentThread == _worker) return func();

        T result = fallback;
        var done = new ManualResetEvent(false); // not disposed here: after a timeout the worker still sets it
        _queue.Add(() =>
        {
            try { result = func(); }
            catch (Exception e) { DebugLog.Write("spelling worker error: " + e.Message); }
            finally { done.Set(); }
        });
        // WaitForSingleObject directly: a managed wait on an STA thread would pump messages
        // and could re-enter the keyboard hook.
        bool ok = WaitForSingleObject(done.SafeWaitHandle.DangerousGetHandle(), TimeoutMs) == 0;
        if (!ok) DebugLog.Write("spelling timed out");
        return ok ? result : fallback;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    private Verdict CheckCore(Layout layout, string word)
    {
        var checker = For(layout);
        if (checker == null) return Verdict.Unknown;
        try
        {
            var errors = checker.Check(word);
            var v = errors.Next() == null ? Verdict.Valid : Verdict.Invalid;
            DebugLog.Write($"  spell {layout.Culture.Name} '{word}' = {v}");
            return v;
        }
        catch (COMException e)
        {
            DebugLog.Write($"  spell {layout.Culture.Name} '{word}' FAILED: 0x{e.HResult:X8} {e.Message}");
            return Verdict.Unknown;
        }
    }

    private ISpellChecker? For(Layout layout)
    {
        if (_checkers.TryGetValue(layout.LangId, out var c)) return c;
        c = null;
        if (_factory != null)
        {
            string? tag = ResolveTag(layout);
            if (tag != null)
            {
                try { c = _factory.CreateSpellChecker(tag); }
                catch (COMException) { c = null; }
            }
        }
        _checkers[layout.LangId] = c;
        return c;
    }

    private string? ResolveTag(Layout layout)
    {
        string full = layout.Culture.Name;               // e.g. "he-IL"
        string lang = layout.Culture.TwoLetterISOLanguageName; // e.g. "he"
        if (string.IsNullOrEmpty(full)) return null;

        foreach (var tag in new[] { full, lang })
            if (SafeIsSupported(tag)) return tag;

        _supported ??= ListSupported();
        return _supported.FirstOrDefault(t => t.StartsWith(lang + "-", StringComparison.OrdinalIgnoreCase));
    }

    private bool SafeIsSupported(string tag)
    {
        try { return _factory!.IsSupported(tag); }
        catch (COMException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private List<string> ListSupported()
    {
        var list = new List<string>();
        try
        {
            IEnumString e = _factory!.SupportedLanguages;
            var one = new string[1];
            while (e.Next(1, one, IntPtr.Zero) == 0) list.Add(one[0]);
        }
        catch (COMException) { }
        return list;
    }

    // ---- COM interop (spellcheck.h) ----

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        IEnumString SupportedLanguages { get; }

        [return: MarshalAs(UnmanagedType.Bool)]
        bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);

        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        string LanguageTag { [return: MarshalAs(UnmanagedType.LPWStr)] get; }

        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        // (remaining methods not needed; vtable prefix is enough)
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        /// <summary>Returns null when there are no more errors (S_FALSE).</summary>
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object? Next();
    }
}
