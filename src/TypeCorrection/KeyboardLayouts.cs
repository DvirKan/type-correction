using System.Globalization;
using System.Text;
using static TypeCorrection.Native;

namespace TypeCorrection;

/// <summary>One physical key press: the key's position (scan code) plus Shift/Caps state.</summary>
internal readonly record struct Stroke(uint Scan, bool Extended, bool Shift, bool Caps);

/// <summary>A keyboard layout installed in Windows (e.g. Hebrew, English US).</summary>
internal sealed class Layout
{
    private readonly Dictionary<Stroke, string> _cache = new();

    public IntPtr Hkl { get; }
    public ushort LangId => (ushort)((long)Hkl & 0xFFFF);
    public CultureInfo Culture { get; }
    public string DisplayName => Culture.NativeName;

    public Layout(IntPtr hkl)
    {
        Hkl = hkl;
        try { Culture = new CultureInfo(LangId); }
        catch (CultureNotFoundException) { Culture = CultureInfo.InvariantCulture; }
    }

    /// <summary>What this key press would type if this layout were active.</summary>
    public string Render(Stroke s)
    {
        if (_cache.TryGetValue(s, out var cached)) return cached;

        string result = "";
        uint vk = MapVirtualKeyEx(s.Scan | (s.Extended ? 0xE000u : 0u), MAPVK_VSC_TO_VK_EX, Hkl);
        if (vk != 0)
        {
            var state = new byte[256];
            if (s.Shift) { state[VK_SHIFT] = 0x80; state[VK_LSHIFT] = 0x80; }
            if (s.Caps) state[VK_CAPITAL] = 0x01;
            var buf = new char[8];
            // flag 0x4: do not touch the system keyboard state (keeps dead keys in the real app intact)
            int n = ToUnicodeEx(vk, s.Scan, state, buf, buf.Length, 0x4, Hkl);
            if (n > 0) result = new string(buf, 0, n);
            else if (n < 0) result = buf[0].ToString(); // dead key
            if (result.Length > 0 && char.IsControl(result[0])) result = "";
        }

        _cache[s] = result;
        return result;
    }

    public string Render(IEnumerable<Stroke> strokes)
    {
        var sb = new StringBuilder();
        foreach (var s in strokes) sb.Append(Render(s));
        return sb.ToString();
    }

    /// <summary>Character → key press that types it in this layout (used for tests).</summary>
    public Dictionary<char, Stroke> BuildReverseMap()
    {
        var map = new Dictionary<char, Stroke>();
        foreach (bool shift in new[] { false, true })
            for (uint scan = 0x02; scan <= 0x35; scan++)
            {
                var stroke = new Stroke(scan, false, shift, false);
                string text = Render(stroke);
                if (text.Length == 1) map.TryAdd(text[0], stroke);
            }
        return map;
    }

    public override string ToString() => $"{DisplayName} (0x{(long)Hkl:X8})";
}

/// <summary>The layouts the user has installed. Refreshed whenever the list changes.</summary>
internal sealed class LayoutSet
{
    private IntPtr[] _hkls = [];

    public IReadOnlyList<Layout> All { get; private set; } = [];

    /// <returns>true if the list changed.</returns>
    public bool Refresh()
    {
        int n = GetKeyboardLayoutList(0, null);
        var arr = new IntPtr[n];
        GetKeyboardLayoutList(n, arr);
        if (arr.SequenceEqual(_hkls)) return false;

        var old = All;
        _hkls = arr;
        All = arr.Select(h => old.FirstOrDefault(l => l.Hkl == h) ?? new Layout(h)).ToList();
        return true;
    }

    public Layout? Find(IntPtr hkl) =>
        All.FirstOrDefault(l => l.Hkl == hkl)
        ?? All.FirstOrDefault(l => l.LangId == (ushort)((long)hkl & 0xFFFF));

    public Layout Next(Layout current)
    {
        int i = -1;
        for (int k = 0; k < All.Count; k++) if (All[k].Hkl == current.Hkl) i = k;
        return All[(i + 1) % All.Count];
    }

    public static IntPtr LayoutOfWindow(IntPtr hwnd) =>
        GetKeyboardLayout(GetWindowThreadProcessId(hwnd, out _));
}
