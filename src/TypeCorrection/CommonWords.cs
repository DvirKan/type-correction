namespace TypeCorrection;

/// <summary>
/// Very frequent short words (≤3 letters) per language. Used only to break ties when a
/// short typed string is technically in the dictionary (e.g. "kt") but the same keys in
/// another layout give a far more likely word (e.g. "לא").
/// Languages without a list simply don't get this extra rule.
/// </summary>
internal static class CommonWords
{
    private static readonly Dictionary<string, HashSet<string>> ByLanguage = new()
    {
        ["he"] = Set("""
            לא כן מה מי של את על זה זו זאת אם גם רק יש אין הוא היא אני אתה הם הן כל עם אז פה שם כי או
            לי לך לו לה לנו לכם להם עוד כבר טוב אבל איך למה מתי היה הנה אחד אחת שתי שני הכל ככה כמו
            אחר רע יום מהר בית דבר אמא אבא אתם אנו מול בלי אצל ליד עד בין כך שלי שלך שלו שלה ממש
            """),
        ["en"] = Set("""
            a i ok is the to of and in it you that he was for on are as with his they at be this have
            from or one had by but not what all were we when your can if will up out so no yes my me
            go hi do did how who why its our us an am get got see now new way may day too any him her
            she has let say she two way use man old big off own lol omg thx pls btw idk
            """),
        ["ru"] = Set("""
            и в не на я что он с как а то все она так его но да ты к у же вы за бы по ее мне было вот
            от меня о из ему мы их нет ну ни там тут где кто
            """),
        ["ar"] = Set("""
            في من على أن لا ما هو هي إلى عن مع هل كل قد لم لن ثم أو بل نعم أنا أنت
            """),
    };

    private static HashSet<string> Set(string words) =>
        new(words.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    public static bool Contains(Layout layout, string word) =>
        ByLanguage.TryGetValue(layout.Culture.TwoLetterISOLanguageName, out var set) && set.Contains(word);
}
