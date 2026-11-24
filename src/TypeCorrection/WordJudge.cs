namespace TypeCorrection;

internal sealed record Decision(Layout Target, string OriginalText, string NewText);

/// <summary>
/// Decides whether a word was typed in the wrong layout.
/// Rule: correct only when the text as typed is NOT a real word, and the same key
/// presses in another installed layout ARE a real word.
/// </summary>
internal sealed class WordJudge
{
    private readonly Spelling _spelling;

    /// <summary>Words the user un-did. Kept in memory only, never written to disk.</summary>
    public HashSet<string> Exceptions { get; } = new(StringComparer.Ordinal);

    public WordJudge(Spelling spelling) => _spelling = spelling;

    public Decision? Decide(IReadOnlyList<Stroke> strokes, Layout source, IReadOnlyList<Layout> layouts)
    {
        if (strokes.Count < 2 || layouts.Count < 2) return null;

        string original = source.Render(strokes);
        if (Exceptions.Contains(original)) return null;

        string core = Core(original);
        Verdict sourceVerdict = Judge(source, core);

        // Dictionaries accept many short abbreviations ("kt" = knot). For a short word
        // that is not a common word, still allow fixing it into a very common word ("kt" → "לא").
        bool shortRare = sourceVerdict == Verdict.Valid && core.Length <= 3 && !CommonWords.Contains(source, core);
        if (sourceVerdict == Verdict.Valid && !shortRare) return null;

        foreach (var target in layouts)
        {
            if (target.Hkl == source.Hkl || target.LangId == source.LangId) continue;

            string text = target.Render(strokes);
            if (text == original) continue;
            string targetCore = Core(text);
            if (targetCore.Length < 2) continue;
            if (Judge(target, targetCore) != Verdict.Valid) continue;
            if (shortRare && !CommonWords.Contains(target, targetCore)) continue;

            // Source language has no dictionary: only correct if the typed text looks like gibberish.
            if (sourceVerdict == Verdict.Unknown && LooksLikeWord(core) && !LooksImplausible(core)) continue;

            return new Decision(target, original, text);
        }
        return null;
    }

    /// <summary>
    /// Context fix: the next word was just corrected into <paramref name="target"/>. If the previous
    /// word was valid in both languages (e.g. "go" = "עם"), return it rendered in the target language.
    /// </summary>
    public string? ConvertPrevious(IReadOnlyList<Stroke> strokes, Layout source, Layout target)
    {
        if (source.LangId == target.LangId) return null;
        string original = source.Render(strokes);
        if (Exceptions.Contains(original)) return null;
        string text = target.Render(strokes);
        string core = Core(text);
        if (text == original || core.Length == 0) return null;
        if (Judge(target, core) != Verdict.Valid) return null;
        // single letters only if they are common words ("ו" is not, "a" is)
        if (core.Length == 1 && !CommonWords.Contains(target, core)) return null;
        return text;
    }

    private Verdict Judge(Layout layout, string core)
    {
        if (core.Length == 0 || !LooksLikeWord(core)) return Verdict.Invalid;
        return _spelling.Check(layout, core);
    }

    /// <summary>Strip punctuation around the word: "(hello," → "hello".</summary>
    internal static string Core(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && !char.IsLetter(s[start])) start++;
        while (end > start && !char.IsLetter(s[end - 1])) end--;
        return s[start..end];
    }

    /// <summary>Only letters from a single script, plus in-word ' " - (don't, צה"ל, e-mail).</summary>
    internal static bool LooksLikeWord(string core)
    {
        Script? script = null;
        foreach (char c in core)
        {
            if (c is '\'' or '"' or '-' or '׳' or '״') continue;
            if (!char.IsLetter(c)) return false;
            var s = ScriptOf(c);
            if (script == null) script = s;
            else if (script != s) return false;
        }
        return script != null;
    }

    private static bool LooksImplausible(string core)
    {
        // Hebrew final letter (ך ם ן ף ץ) in the middle of a word
        for (int i = 0; i < core.Length - 1; i++)
            if ("ךםןףץ".Contains(core[i]) && char.IsLetter(core[i + 1])) return true;

        // Latin word of 3+ letters with no vowel
        if (core.Length >= 3 && core.All(c => ScriptOf(c) == Script.Latin)
            && !core.Any(c => "aeiouyAEIOUY".Contains(c))) return true;

        return false;
    }

    private enum Script { Latin, Greek, Cyrillic, Hebrew, Arabic, Other }

    private static Script ScriptOf(char c) => c switch
    {
        < 'ɐ' => Script.Latin,
        >= 'Ͱ' and < 'Ѐ' => Script.Greek,
        >= 'Ѐ' and < '԰' => Script.Cyrillic,
        >= '֐' and < '؀' => Script.Hebrew,
        >= '؀' and < '܀' => Script.Arabic,
        _ => Script.Other,
    };
}
