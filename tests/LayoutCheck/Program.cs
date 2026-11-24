using System.Text;
using TypeCorrection;

// Checks the correction logic against the layouts installed on this machine,
// without hooking the keyboard. Exit code 1 if any expectation fails.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var layouts = new LayoutSet();
        layouts.Refresh();
        var spelling = new Spelling();
        var judge = new WordJudge(spelling);

        Console.WriteLine("Installed layouts:");
        foreach (var l in layouts.All)
            Console.WriteLine($"  {l}  culture={l.Culture.Name}  dictionary={(spelling.HasDictionary(l) ? "yes" : "NO")}");
        Console.WriteLine();

        var words = new Dictionary<string, string[]>
        {
            ["he"] = ["סבבה", "שלום", "לא", "מה", "תודה", "אני", "בבקשה", "מחשב", "שלום.", "היום", "אוניברסיטה", "עכשיו",
                      "כן", "של", "זה", "גם", "יש", "עם", "הוא", "אבל", "למה", "צה\"ל", "מתכנת", "ירושלים"],
            ["en"] = ["hello", "ok", "the", "computer", "yes", "what?", "don't", "keyboard", "is", "university",
                      "a", "to", "and", "you", "it", "not", "programming", "github"],
        };
        if (args.Length > 0) words = new() { [args[0]] = args[1..] };

        // Real words in BOTH languages (or too short / not in the dictionary): a lone word must be left alone.
        var ambiguous = new HashSet<string> { "גם", "עם", "to", "a", "github" };

        int fail = 0;
        foreach (var (lang, list) in words)
        {
            var intended = layouts.All.FirstOrDefault(l => l.Culture.TwoLetterISOLanguageName == lang);
            if (intended == null) { Console.WriteLine($"[skip] no '{lang}' layout installed"); continue; }
            var reverse = intended.BuildReverseMap();

            foreach (var word in list)
            {
                if (!word.All(reverse.ContainsKey)) { Console.WriteLine($"[skip] can't type '{word}'"); continue; }
                var strokes = word.Select(c => reverse[c]).ToList();

                // 1) typed in the right layout → must stay as is
                var same = judge.Decide(strokes, intended, layouts.All);
                bool okSame = same == null;

                // 2) typed in each wrong layout → must become the intended word
                foreach (var wrong in layouts.All.Where(l => l.LangId != intended.LangId))
                {
                    var d = judge.Decide(strokes, wrong, layouts.All);
                    string typed = wrong.Render(strokes);
                    string expected = ambiguous.Contains(word) ? typed : word;
                    string got = d?.NewText ?? typed;
                    bool ok = got == expected;
                    if (!ok) fail++;
                    Console.WriteLine($"{(ok ? "OK  " : "FAIL")} typed '{typed}' ({wrong.Culture.Name}) → '{got}'   expected '{expected}'{(ambiguous.Contains(word) ? "  (ambiguous alone)" : "")}");
                }
                if (!okSame) fail++;
                Console.WriteLine($"{(okSame ? "OK  " : "FAIL")} typed '{word}' correctly → {(okSame ? "left alone" : "WRONGLY changed to '" + same!.NewText + "'")}");
            }
        }

        // Context fix: "go ,usv" → the next word becomes "תודה", so "go" should become "עם".
        var he = layouts.All.FirstOrDefault(l => l.Culture.TwoLetterISOLanguageName == "he");
        var en = layouts.All.FirstOrDefault(l => l.Culture.TwoLetterISOLanguageName == "en");
        if (he != null && en != null)
        {
            Console.WriteLine();
            var heMap = he.BuildReverseMap();
            var enMap = en.BuildReverseMap();
            foreach (var (prevWord, prevLayout, target, expected) in new[]
            {
                ("עם", he, he, "עם"), ("גם", he, he, "גם"), ("to", en, en, "to"),
                ("ו", he, he, null), // single non-common letter: don't touch
            })
            {
                var map = prevLayout == he ? heMap : enMap;
                var strokes = prevWord.Select(c => map[c]).ToList();
                var typedIn = prevLayout == he ? en : he; // typed in the wrong layout
                string? got = judge.ConvertPrevious(strokes, typedIn, target);
                bool ok = got == expected;
                if (!ok) fail++;
                Console.WriteLine($"{(ok ? "OK  " : "FAIL")} context: previous '{typedIn.Render(strokes)}' → '{got ?? "(no change)"}'   expected '{expected ?? "(no change)"}'");
            }
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "All checks passed." : $"{fail} check(s) failed.");
        return fail == 0 ? 0 : 1;
    }
}
