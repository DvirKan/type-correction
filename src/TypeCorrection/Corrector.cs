using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static TypeCorrection.Native;

namespace TypeCorrection;

/// <summary>
/// Watches key presses system-wide, collects the current word (in memory only),
/// and when the word ends (Space / Enter) replaces it if it was typed in the wrong layout.
///
/// Privacy: only the current word and the last word are held, as key positions, and
/// they are dropped on the next word / click / window switch. Nothing is written or sent.
/// </summary>
internal sealed class Corrector : IDisposable
{
    private readonly LayoutSet _layouts;
    private readonly WordJudge _judge;
    private readonly Settings _settings;
    private readonly Control _ui;
    private readonly HookProc _keyProc, _mouseProc; // keep delegates alive
    private IntPtr _keyHook, _mouseHook;

    // current word
    private readonly List<Stroke> _word = new();
    private IntPtr _wordHwnd, _wordHkl;
    private bool _wordBlocked;

    // last finished word (for Backspace-undo and the Pause hotkey)
    private sealed class LastWord
    {
        public required List<Stroke> Strokes;
        public required Layout Layout;       // layout of the text now on screen
        public required string Text;         // text now on screen (without the trailing space)
        public required IntPtr Hwnd;
        public Layout? UndoLayout;           // set if we auto-corrected it
        public string? UndoText;
    }
    private LastWord? _last;
    private LastWord? _previous; // the word before the current one (for the context fix)

    private readonly Dictionary<uint, string> _processNames = new();

    public bool Enabled { get; set; } = true;

    public Corrector(LayoutSet layouts, WordJudge judge, Settings settings, Control ui)
    {
        _layouts = layouts;
        _judge = judge;
        _settings = settings;
        _ui = ui;
        _keyProc = KeyProc;
        _mouseProc = MouseProc;
    }

    public void Start()
    {
        IntPtr mod = GetModuleHandle(null);
        _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, mod, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
        if (_keyHook == IntPtr.Zero)
            throw new InvalidOperationException($"Keyboard hook failed (error {Marshal.GetLastWin32Error()}).");
    }

    public void Dispose()
    {
        if (_keyHook != IntPtr.Zero) UnhookWindowsHookEx(_keyHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _keyHook = _mouseHook = IntPtr.Zero;
        ResetAll();
    }

    // ------------------------------------------------------------------ hooks

    private IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // Ignore synthetic input (ours and other tools'), react only to key-down.
            if ((k.flags & LLKHF_INJECTED) == 0 && (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN))
            {
                try
                {
                    if (OnKeyDown(k)) return 1; // swallow the key
                }
                catch (Exception ex)
                {
                    DebugLog.Write("EXCEPTION in key handler: " + ex);
                    ResetAll();
                }
            }
        }
        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
            ResetAll(); // caret probably moved
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    // ------------------------------------------------------------------ logic

    /// <returns>true to swallow the key.</returns>
    private bool OnKeyDown(KBDLLHOOKSTRUCT k)
    {
        int vk = (int)k.vkCode;

        switch (vk)
        {
            case VK_SHIFT or VK_LSHIFT or VK_RSHIFT or VK_CAPITAL
                or VK_CONTROL or VK_LCONTROL or VK_RCONTROL
                or VK_MENU or VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN:
                return false; // modifier alone
        }

        bool ctrl = IsDown(VK_CONTROL);
        bool alt = (k.flags & LLKHF_ALTDOWN) != 0 || IsDown(VK_MENU);
        bool win = IsDown(VK_LWIN) || IsDown(VK_RWIN);
        if (ctrl || alt || win || !Enabled) { ResetAll(); return false; } // shortcut, not typing

        IntPtr hwnd = GetForegroundWindow();
        if (_word.Count > 0 && hwnd != _wordHwnd) _word.Clear();
        if (_last != null && hwnd != _last.Hwnd) _last = null;

        switch (vk)
        {
            case VK_PAUSE: return ManualConvert(hwnd);
            case VK_BACK: return OnBackspace();
            case VK_SPACE: return OnWordEnd(hwnd, VK_SPACE);
            case VK_RETURN: return OnWordEnd(hwnd, VK_RETURN);
            case VK_TAB or VK_ESCAPE or VK_DELETE or VK_INSERT or VK_APPS
                or VK_PRIOR or VK_NEXT or VK_END or VK_HOME
                or VK_LEFT or VK_UP or VK_RIGHT or VK_DOWN:
            case >= VK_F1 and <= VK_F24:
                ResetAll();
                return false;
        }

        if (_word.Count == 0) BeginWord(hwnd);
        if (_wordBlocked) return false;

        var stroke = new Stroke(k.scanCode, (k.flags & LLKHF_EXTENDED) != 0,
            IsDown(VK_SHIFT), (GetKeyState(VK_CAPITAL) & 1) != 0);
        var source = _layouts.Find(_wordHkl);
        DebugLog.Write($"key vk=0x{vk:X2} scan=0x{k.scanCode:X2} flags=0x{k.flags:X} {stroke} → '{source?.Render(stroke)}'");
        if (source == null || source.Render(stroke).Length == 0) { ResetAll(); return false; }

        _word.Add(stroke);
        if (_word.Count > 40) ResetAll(); // not a word
        return false;
    }

    private void BeginWord(IntPtr hwnd)
    {
        _layouts.Refresh();
        _previous = _last != null && _last.Hwnd == hwnd && _last.UndoText == null ? _last : null;
        _last = null;
        _wordHwnd = hwnd;
        _wordHkl = LayoutSet.LayoutOfWindow(hwnd);
        _wordBlocked = IsBlocked(hwnd);
        GetWindowThreadProcessId(hwnd, out uint pid);
        DebugLog.Write($"word start: app={ProcessName(pid)} hwnd=0x{(long)hwnd:X} hkl=0x{(long)_wordHkl:X} " +
                       $"layout={_layouts.Find(_wordHkl)} blocked={_wordBlocked} layouts={_layouts.All.Count}");
    }

    private bool OnWordEnd(IntPtr hwnd, int boundaryVk)
    {
        if (_word.Count == 0 || _wordBlocked) { ResetAll(); return false; }

        var strokes = _word.ToList();
        _word.Clear();
        var source = _layouts.Find(_wordHkl);
        if (source == null) { _last = null; return false; }

        var previous = _previous;
        _previous = null;

        var d = _judge.Decide(strokes, source, _layouts.All);
        DebugLog.Write($"word end: typed '{source.Render(strokes)}' in {source} → " +
                       (d == null ? "no change" : $"'{d.NewText}' ({d.Target})") +
                       "  | " + string.Join(", ", _layouts.All.Select(l => $"{l.Culture.Name}:'{l.Render(strokes)}'")));
        if (d == null)
        {
            _last = boundaryVk == VK_SPACE
                ? new LastWord { Strokes = strokes, Layout = source, Text = source.Render(strokes), Hwnd = hwnd }
                : null;
            return false;
        }

        _last = boundaryVk == VK_SPACE
            ? new LastWord
            {
                Strokes = strokes, Layout = d.Target, Text = d.NewText, Hwnd = hwnd,
                UndoLayout = source, UndoText = d.OriginalText,
            }
            : null;

        // Context fix: "go ,usv" → "עם תודה" (the previous word was a real word in both languages).
        int delete = d.OriginalText.Length;
        string insert = d.NewText;
        string? prevFixed = previous == null ? null : _judge.ConvertPrevious(previous.Strokes, previous.Layout, d.Target);
        if (previous != null && prevFixed != null)
        {
            delete += previous.Text.Length + 1;
            insert = prevFixed + " " + d.NewText;
            if (_last != null) { _last.UndoText = previous.Text + " " + d.OriginalText; _last.Text = insert; }
        }

        // The Space/Enter is swallowed; we re-send it after the fixed word,
        // so the app (chat, Word autocorrect...) only ever sees the fixed word.
        Post(() => Replace(hwnd, delete, insert, boundaryVk, d.Target.Hkl));
        return true;
    }

    private bool OnBackspace()
    {
        // Backspace is always a normal Backspace (undo lives on the Pause key).
        if (_word.Count > 0) _word.RemoveAt(_word.Count - 1);
        else _last = null;
        return false;
    }

    /// <summary>Pause key: convert the current (or just-finished) word to the next layout.</summary>
    private bool ManualConvert(IntPtr hwnd)
    {
        _layouts.Refresh();
        if (_layouts.All.Count < 2) return false;

        if (_word.Count > 0 && !_wordBlocked)
        {
            var source = _layouts.Find(_wordHkl);
            if (source == null) return false;
            var next = _layouts.Next(source);
            string from = source.Render(_word), to = next.Render(_word);
            _wordHkl = next.Hkl;
            Post(() => Replace(hwnd, from.Length, to, 0, next.Hkl));
            return true;
        }

        if (_last is { UndoText: not null, UndoLayout: not null } corrected)
        {
            // Pause right after an auto-correction = undo it, and don't auto-fix those words again
            // (in memory only, until the app restarts).
            foreach (var w in corrected.UndoText.Split(' ')) _judge.Exceptions.Add(w);
            int del = corrected.Text.Length + 1;
            string original = corrected.UndoText;
            var layout = corrected.UndoLayout;
            corrected.Text = original;
            corrected.Layout = layout;
            corrected.UndoText = null;
            corrected.UndoLayout = null;
            DebugLog.Write($"undo → '{original}'");
            Post(() => Replace(hwnd, del, original, VK_SPACE, layout.Hkl));
            return true;
        }

        if (_last != null)
        {
            var last = _last;
            var next = _layouts.Next(last.Layout);
            string to = next.Render(last.Strokes);
            int delete = last.Text.Length + 1;
            last.Layout = next;
            last.Text = to;
            last.UndoText = null;
            last.UndoLayout = null;
            Post(() => Replace(hwnd, delete, to, VK_SPACE, next.Hkl));
            return true;
        }
        return false;
    }

    private void ResetAll()
    {
        _word.Clear();
        _last = null;
        _wordBlocked = false;
    }

    // ------------------------------------------------------------------ helpers

    private void Post(Action action)
    {
        // Run after the hook returns (sending input from inside the hook is unreliable).
        if (_ui.IsHandleCreated) _ui.BeginInvoke(action);
    }

    private static void Replace(IntPtr hwnd, int deleteCount, string text, int boundaryVk, IntPtr newHkl)
    {
        if (newHkl != IntPtr.Zero)
            PostMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, newHkl);

        var inputs = new List<INPUT>();
        for (int i = 0; i < deleteCount; i++) AddVk(inputs, VK_BACK);
        foreach (char c in text) AddChar(inputs, c);
        if (boundaryVk != 0) AddVk(inputs, boundaryVk);
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private static void AddVk(List<INPUT> list, int vk)
    {
        foreach (uint flags in new[] { 0u, KEYEVENTF_KEYUP })
            list.Add(new INPUT { type = INPUT_KEYBOARD, U = { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags } } });
    }

    private static void AddChar(List<INPUT> list, char c)
    {
        foreach (uint flags in new[] { KEYEVENTF_UNICODE, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP })
            list.Add(new INPUT { type = INPUT_KEYBOARD, U = { ki = new KEYBDINPUT { wScan = c, dwFlags = flags } } });
    }

    /// <summary>Password field, or an app the user excluded → don't even collect the word.</summary>
    private bool IsBlocked(IntPtr hwnd)
    {
        uint tid = GetWindowThreadProcessId(hwnd, out uint pid);

        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(tid, ref gti) && gti.hwndFocus != IntPtr.Zero)
        {
            long style = GetWindowLongPtr(gti.hwndFocus, GWL_STYLE).ToInt64();
            if ((style & ES_PASSWORD) != 0)
            {
                var cls = new StringBuilder(64);
                GetClassName(gti.hwndFocus, cls, cls.Capacity);
                if (cls.ToString().Contains("Edit", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        string? name = ProcessName(pid);
        return name != null && _settings.IsExcluded(name);
    }

    private string? ProcessName(uint pid)
    {
        if (_processNames.TryGetValue(pid, out var n)) return n;
        if (_processNames.Count > 256) _processNames.Clear();
        try
        {
            using var p = Process.GetProcessById((int)pid);
            n = p.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { n = null; }
        if (n != null) _processNames[pid] = n;
        return n;
    }
}
