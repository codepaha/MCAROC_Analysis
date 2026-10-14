using System.Text;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>
/// The sound of a company name reduced to its consonants, so that spellings of the same name compare equal (#489): <c>Laxmi</c> and <c>Lakshmi</c>,
/// <c>Hi-Tech</c> and <c>Hitech</c>, and a Devanagari spelling (after <see cref="Transliterate"/>) and its English original.
/// <para>Vowels are dropped (Hindi and English spell them differently: प्रोपल्शन is "propalshan", the English is "propulsion"), so are <c>h</c> (aspirates and silent h)
/// and <c>y</c>. Doubled consonants count once; x = ks, ph = f, w = v, z = j, q = k, soft c = s, hard c = k, -tion = -shn. A trailing legal form (Private Limited, Pvt Ltd,
/// LLP, Company, and their Devanagari spellings, which sound the same) and a leading M/s are not part of the name.</para>
/// <para>The key is for checking that two spellings are the same name, never for deciding that a party is the company: that needs the whole-name rule or a judgement.</para>
/// </summary>
public static class NamePhoneticKey
{
    private static readonly string[] LegalFormWords = ["private", "pvt", "limited", "ltd", "llp", "company", "co", "corporation", "corp", "inc", "the"];
    private static readonly HashSet<string> LegalFormKeys = LegalFormWords.Select(WordKey).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
    private static readonly string MessrsKey = WordKey("messrs");

    /// <summary>The key of a name in Latin or Devanagari script (Devanagari is transliterated first).</summary>
    public static string Of(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var latin = Transliterate(name).ToLowerInvariant().Replace("&", " and ").Replace("m/s", " ").Replace("m/ s", " ");
        var words = Words(latin).ToList();
        while (words.Count > 1 && IsLegalForm(words[^1])) words.RemoveAt(words.Count - 1);
        if (words.Count > 1 && WordKey(words[0]) == MessrsKey) words.RemoveAt(0);
        var key = Collapse(string.Concat(words.Select(WordKey)));
        return key;
    }

    /// <summary>True when the two names sound the same: equal keys, or for a long name (6+ consonants) keys one edit apart, which absorbs a single
    /// transliteration choice (ch/k in "Tech") without letting a different or truncated name through.</summary>
    public static bool SameName(string? a, string? b)
    {
        foreach (var ka in KeysOf(a))
        foreach (var kb in KeysOf(b))
        {
            if (ka == kb) return true;
            if (Math.Min(ka.Length, kb.Length) >= 6 && EditDistanceAtMostOne(ka, kb)) return true;
        }
        return false;
    }

    /// <summary>The key of a name, and a second one with its acronyms said letter by letter: Hindi writes HDFC as एचडीएफसी ("ech di ef si"), not as a word.
    /// An acronym is a 2-6 letter word with no vowel (HDFC, BPCL); in an all-capitals name a real word nearly always has one.</summary>
    internal static IEnumerable<string> KeysOf(string? name)
    {
        var plain = Of(name);
        if (plain.Length == 0) yield break;
        yield return plain;
        if (string.IsNullOrWhiteSpace(name) || IsDevanagari(name)) yield break;
        var spelled = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(SpellIfAcronym));
        var spelledKey = Of(spelled);
        if (spelledKey.Length > 0 && spelledKey != plain) yield return spelledKey;
    }

    private static readonly Dictionary<char, string> LetterNames = new()
    {
        ['A'] = "e", ['B'] = "bi", ['C'] = "si", ['D'] = "di", ['E'] = "i", ['F'] = "ef", ['G'] = "ji", ['H'] = "ech", ['I'] = "ai", ['J'] = "je", ['K'] = "ke",
        ['L'] = "el", ['M'] = "em", ['N'] = "en", ['O'] = "o", ['P'] = "pi", ['Q'] = "kyu", ['R'] = "ar", ['S'] = "es", ['T'] = "ti", ['U'] = "yu", ['V'] = "vi",
        ['W'] = "dablyu", ['X'] = "eks", ['Y'] = "vai", ['Z'] = "jed",
    };

    private static string SpellIfAcronym(string word)
    {
        var letters = word.Where(char.IsLetter).ToArray();
        if (letters.Length is < 2 or > 6 || letters.Length != word.Count(c => c != '.')) return word;
        if (letters.Any(c => "AEIOUaeiou".Contains(c))) return word;
        return string.Concat(letters.Select(c => LetterNames.TryGetValue(char.ToUpperInvariant(c), out var n) ? n : c.ToString()));
    }

    private static bool IsLegalForm(string word) => LegalFormKeys.Contains(WordKey(word)) || LegalFormWords.Contains(word);

    private static IEnumerable<string> Words(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is '-' or '\'') continue;   // Hi-Tech = HiTech
            else if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>The consonant skeleton of one lower-case Latin word.</summary>
    internal static string WordKey(string word)
    {
        var w = word.ToLowerInvariant();
        w = w.Replace("tio", "sh").Replace("sio", "sh").Replace("ck", "k").Replace("ph", "f").Replace("x", "ks").Replace("q", "k");
        var sb = new StringBuilder();
        for (var i = 0; i < w.Length; i++)
        {
            var c = w[i];
            if (char.IsDigit(c)) { sb.Append(c); continue; }
            switch (c)
            {
                case 'a' or 'e' or 'i' or 'o' or 'u' or 'h' or 'y': continue;
                case 'c': sb.Append(i + 1 < w.Length && w[i + 1] is 'e' or 'i' or 'y' ? 's' : 'k'); continue;
                case 'w': sb.Append('v'); continue;
                case 'z': sb.Append('j'); continue;
                case 'm' when i + 1 < w.Length && w[i + 1] is 'b' or 'p': sb.Append('n'); continue;   // Ambika = अंबिका (anusvara)
                default:
                    if (c is >= 'a' and <= 'z') sb.Append(c);
                    continue;
            }
        }
        return Collapse(sb.ToString());
    }

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (sb.Length == 0 || sb[^1] != c || char.IsDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    private static bool EditDistanceAtMostOne(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return false;
        int i = 0, j = 0, edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (a.Length > b.Length) i++;
            else if (b.Length > a.Length) j++;
            else { i++; j++; }
        }
        return edits + (a.Length - i) + (b.Length - j) <= 1;
    }

    // ---- Devanagari -> Latin -------------------------------------------------------------------------------------------------------------

    private static readonly Dictionary<char, string> Consonants = new()
    {
        ['क'] = "k", ['ख'] = "kh", ['ग'] = "g", ['घ'] = "gh", ['ङ'] = "n",
        ['च'] = "ch", ['छ'] = "chh", ['ज'] = "j", ['झ'] = "jh", ['ञ'] = "n",
        ['ट'] = "t", ['ठ'] = "th", ['ड'] = "d", ['ढ'] = "dh", ['ण'] = "n",
        ['त'] = "t", ['थ'] = "th", ['द'] = "d", ['ध'] = "dh", ['न'] = "n",
        ['प'] = "p", ['फ'] = "ph", ['ब'] = "b", ['भ'] = "bh", ['म'] = "m",
        ['य'] = "y", ['र'] = "r", ['ल'] = "l", ['ळ'] = "l", ['व'] = "v",
        ['श'] = "sh", ['ष'] = "sh", ['स'] = "s", ['ह'] = "h",
        // precomposed nukta letters
        ['क़'] = "q", ['ख़'] = "kh", ['ग़'] = "g", ['ज़'] = "z", ['ड़'] = "r", ['ढ़'] = "rh", ['फ़'] = "f", ['य़'] = "y",
    };

    /// <summary>A consonant followed by the combining nukta (U+093C) sounds as these.</summary>
    private static readonly Dictionary<char, string> NuktaForms = new() { ['क'] = "q", ['ज'] = "z", ['ड'] = "r", ['ढ'] = "rh", ['फ'] = "f", ['ग'] = "g", ['ख'] = "kh", ['य'] = "y" };

    private static readonly Dictionary<char, string> IndependentVowels = new()
    {
        ['अ'] = "a", ['आ'] = "aa", ['इ'] = "i", ['ई'] = "ii", ['उ'] = "u", ['ऊ'] = "uu", ['ऋ'] = "ri", ['ए'] = "e", ['ऐ'] = "ai",
        ['ओ'] = "o", ['औ'] = "au", ['ऑ'] = "o", ['ऍ'] = "e",
    };

    private static readonly Dictionary<char, string> Matras = new()
    {
        ['ा'] = "aa", ['ि'] = "i", ['ी'] = "ii", ['ु'] = "u", ['ू'] = "uu", ['ृ'] = "ri", ['े'] = "e", ['ै'] = "ai", ['ो'] = "o", ['ौ'] = "au", ['ॉ'] = "o", ['ॅ'] = "e",
    };

    private const char Virama = '्';
    private const char Nukta = '़';

    /// <summary>Devanagari letters written in Latin (a simple, deterministic scheme; Latin text passes through unchanged). Readable enough for an audit line,
    /// exact enough for <see cref="Of"/>, which keeps only consonants.</summary>
    public static string Transliterate(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (Consonants.TryGetValue(c, out var cons))
            {
                if (i + 1 < text.Length && text[i + 1] == Nukta && NuktaForms.TryGetValue(c, out var nukta)) { cons = nukta; i++; }
                sb.Append(cons);
                var next = i + 1 < text.Length ? text[i + 1] : '\0';
                if (next == Virama) { i++; continue; }
                if (Matras.TryGetValue(next, out var matra)) { sb.Append(matra); i++; continue; }
                sb.Append('a');   // the inherent vowel
                continue;
            }
            if (IndependentVowels.TryGetValue(c, out var vowel)) { sb.Append(vowel); continue; }
            if (Matras.TryGetValue(c, out var stray)) { sb.Append(stray); continue; }
            switch (c)
            {
                case 'ं' or 'ँ': sb.Append('n'); continue;    // anusvara, chandrabindu
                case 'ः': sb.Append('h'); continue;            // visarga
                case Virama or Nukta: continue;
                case '।' or '॥': sb.Append(' '); continue;
            }
            if (c is >= '०' and <= '९') { sb.Append((char)('0' + (c - '०'))); continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>True when most of the letters are Devanagari.</summary>
    public static bool IsDevanagari(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        int deva = 0, letters = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c) && !(c >= 'ऀ' && c <= 'ॿ')) continue;
            letters++;
            if (c >= 'ऀ' && c <= 'ॿ') deva++;
        }
        return letters > 0 && deva * 2 > letters;
    }
}
