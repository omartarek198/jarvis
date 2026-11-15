using System.Text.RegularExpressions;

namespace Jarvis.Kernel.Services;

/// <summary>
/// The full console-text → ear-text pipeline, in one ordered pass. Wraps the existing
/// SpeechText sanitizer (URLs/paths/hex-ids/markdown/length-cap) and adds pronunciation
/// rules: acronyms, ticket ids, ports, durations, symbols, stack-trace suppression.
/// Rules are deliberately dumb regexes — prefer adding a "lexicon" entry in
/// voice/profiles.json over adding code here.
/// </summary>
public static class SpeechTextFormatter
{
    /// <summary>Bump when pipeline output can change — it is part of the phrase-cache key.</summary>
    public const int Version = 1;

    // Built-in pronunciation lexicon (regex → replacement). Case-sensitive on purpose:
    // we only respell SHOUTING acronyms, never lowercase words that happen to collide.
    // profiles.json "lexicon" entries run first and win on conflict.
    private static readonly (string Pattern, string Replacement)[] Builtin =
    [
        (@"\bJARVIS\b", "Jarvis"),
        (@"\bAPIs\b", "A P I's"),
        (@"\bAPI\b", "A P I"),
        (@"\bURLs\b", "U R L's"),
        (@"\bURL\b", "U R L"),
        (@"\bUI\b", "U I"),
        (@"\bUX\b", "U X"),
        (@"\bCLI\b", "C L I"),
        (@"\bCPU\b", "C P U"),
        (@"\bGPU\b", "G P U"),
        (@"\bPRs\b", "P R's"),
        (@"\bPR\b", "P R"),
        (@"\bDB\b", "D B"),
        (@"\bQA\b", "Q A"),
        (@"\bAI\b", "A I"),
        (@"\bIDs\b", "I D's"),
        (@"\bSDK\b", "S D K"),
        (@"\bMCP\b", "M C P"),
        (@"\bTTS\b", "T T S"),
        (@"\bSTT\b", "S T T"),
        (@"\bLLMs\b", "L L M's"),
        (@"\bLLM\b", "L L M"),
        (@"\bHTTPS\b", "H T T P S"),
        (@"\bHTTP\b", "H T T P"),
        (@"\bJSON\b", "jason"),
        (@"\bSQL\b", "sequel"),
        (@"\bIoT\b", "I O T"),
        (@"\bEF\b", "E F"),
    ];

    /// <summary>
    /// Format text for the ear. <paramref name="maxChars"/> caps spoken length (the console
    /// always shows the full text). <paramref name="lexicon"/> is the hand-editable map from
    /// voice/profiles.json; it is applied before the built-in rules and wins on conflict.
    /// Paragraph breaks survive as single '\n' so the chunker can insert longer pauses.
    /// </summary>
    public static string Format(string text, int maxChars = 420, IReadOnlyDictionary<string, string>? lexicon = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var r = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // Never read code or stack traces at a human
        r = Regex.Replace(r, @"```+[\s\S]*?```+", " Code omitted. ");
        r = Regex.Replace(r, @"(?m)^[ \t]+at\s+\S+\(.*$", " ");                 // .NET stack frames
        r = Regex.Replace(r, @"(?m)^[ \t]*File ""[^""]+"", line \d+.*$", " ");  // Python stack frames
        r = Regex.Replace(r, @"\b[A-Za-z][\w.]{2,}(?:Exception|Error)\b", "an error");
        r = Regex.Replace(r,
            @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", "an id");

        // Pronunciation lexicon: user entries (profiles.json) first, built-ins after
        if (lexicon is not null)
            foreach (var (k, v) in lexicon)
                r = Regex.Replace(r, $@"(?<![\w-]){Regex.Escape(k)}(?![\w-])", v);
        foreach (var (p, v) in Builtin)
            r = Regex.Replace(r, p, v);

        // Jira-style ticket ids: ABC-12 → "A B C 12" (Normalize below words small numbers)
        r = Regex.Replace(r, @"\b([A-Z]{2,6})-(\d{1,5})\b", m => Spell(m.Groups[1].Value) + " " + m.Groups[2].Value);

        // localhost:5588 → "local host, port fifty five eighty eight"
        // (lookbehind skips full URLs — SpeechText.ForSpeech already turns those into "link")
        r = Regex.Replace(r, @"(?<![/\w.])(?:localhost|127\.0\.0\.1):(\d{2,5})\b",
            m => "local host, port " + PortWords(m.Groups[1].Value));

        // Durations: "0h-07m-12s" → "seven minutes, twelve seconds"
        r = Regex.Replace(r, @"\b(?<h>\d+)h[-\s](?<m>\d{1,2})m(?:[-\s](?<s>\d{1,2})s)?\b", DurationWords);
        r = Regex.Replace(r, @"\b(?<m>\d{1,2})m[-\s](?<s>\d{1,2})s\b", DurationWords);

        // Symbols that read terribly out loud
        r = Regex.Replace(r, @"(\d)\s*%", "$1 percent");
        r = r.Replace("%", " percent");
        r = Regex.Replace(r, @"\s*(->|=>|→)\s*", " to ");
        r = Regex.Replace(r, @"\s*&&\s*", " and ");
        r = Regex.Replace(r, @"\s*\|\|\s*", " or ");
        r = Regex.Replace(r, @"#(\d+)\b", "number $1");
        r = Regex.Replace(r, @"\b([A-Za-z]+)_(\w+)\b", m => m.Value.Replace('_', ' '));  // switch_voice → switch voice

        // Per paragraph: existing sanitizer (URLs, emails, paths, hex ids, markdown), bullets stripped.
        // Length cap is applied once over the whole reply, below — not per paragraph.
        var paragraphs = r.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => Regex.Replace(p, @"^\s*[-*•]\s+", ""))
            .Select(p => SpeechText.ForSpeech(p, maxChars: int.MaxValue / 4))
            .Where(p => p.Length > 0);
        r = string.Join("\n", paragraphs);

        r = Cap(r, maxChars);
        return SpeechText.Normalize(r);
    }

    /// <summary>Same console-pointer cut SpeechText.ForSpeech used, applied over the whole reply.</summary>
    private static string Cap(string r, int maxChars)
    {
        if (maxChars <= 0 || r.Length <= maxChars) return r;
        var cut = r.LastIndexOfAny(['.', '!', '?'], maxChars - 1);
        return (cut > maxChars / 2 ? r[..(cut + 1)] : r[..maxChars].TrimEnd() + ".")
               + " There's more, it's in the console.";
    }

    private static string Spell(string s) => string.Join(" ", s.ToCharArray());

    private static readonly string[] Ones =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
    ];
    private static readonly string[] Tens =
        ["", "ten", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    private static string TwoDigit(int n) =>
        n < 20 ? Ones[n] : Tens[n / 10] + (n % 10 > 0 ? " " + Ones[n % 10] : "");

    /// <summary>Read a port the way humans do: 5588 → "fifty five eighty eight", 8007 → "eighty oh seven".</summary>
    private static string PortWords(string digits)
    {
        var parts = new List<string>();
        var i = 0;
        if (digits.Length % 2 == 1) { parts.Add(Ones[digits[0] - '0']); i = 1; }
        for (; i + 1 < digits.Length; i += 2)
        {
            var pair = (digits[i] - '0') * 10 + (digits[i + 1] - '0');
            parts.Add(digits[i] == '0'
                ? (pair == 0 ? "zero zero" : "oh " + Ones[pair])
                : TwoDigit(pair));
        }
        return string.Join(" ", parts);
    }

    private static string DurationWords(Match m)
    {
        var parts = new List<string>();
        void Add(string group, string unit)
        {
            if (!m.Groups[group].Success) return;
            var n = int.Parse(m.Groups[group].Value);
            if (n > 0) parts.Add($"{n} {unit}{(n == 1 ? "" : "s")}");
        }
        Add("h", "hour"); Add("m", "minute"); Add("s", "second");
        return parts.Count == 0 ? "zero seconds" : string.Join(", ", parts);
    }
}
