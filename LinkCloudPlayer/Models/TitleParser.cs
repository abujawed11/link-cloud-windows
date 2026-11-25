using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace LinkCloudPlayer.Models;

/// <summary>
/// Turns scene-style file names into a display title, e.g.
/// "Sita.Ramam.2022.1080p.AMZN.WEB-DL.Hindi-Telugu.DDP5.1.H.264.mkv"
///   → Title "Sita Ramam", Year 2022, Quality "1080p", Languages [Hindi, Telugu].
/// </summary>
public static partial class TitleParser
{
    public record Result(string Title, int? Year, string? Quality, IReadOnlyList<string> Languages);

    [GeneratedRegex(@"(?<!\d)(19[2-9]\d|20[0-4]\d)(?!\d)")]
    private static partial Regex YearRx();

    [GeneratedRegex(@"\b(2160p|1080p|720p|576p|480p|4k|uhd)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualityRx();

    // Release/encoding tags that mark where the real title ends when there is no year.
    [GeneratedRegex(@"\b(2160p|1080p|720p|576p|480p|4k|uhd|web[- ]?dl|web[- ]?rip|bluray|blu[- ]ray|brrip|bdrip|hdrip|dvdrip|dvdscr|hdtv|hdcam|camrip|x264|x265|h ?26[45]|hevc|10bit|aac|ac3|ddp?\d|dts|amzn|nf|dsnp|hmax|dual audio|multi audio|uncut|proper|repack|hindi|tamil|telugu|english|malayalam|kannada|bengali|marathi)\b", RegexOptions.IgnoreCase)]
    private static partial Regex JunkRx();

    // "www.SiteName.com - Movie..." / "[Group] Movie..."
    [GeneratedRegex(@"^\s*(www\.[\w-]+\.\w+|[\w-]+\.(com|org|net|in|io|me|to|cc|xyz))\s*-\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SitePrefixRx();

    [GeneratedRegex(@"^\s*\[[^\]]*\]\s*")]
    private static partial Regex BracketPrefixRx();

    private static readonly string[] KnownLanguages =
    [
        "Hindi", "English", "Tamil", "Telugu", "Malayalam", "Kannada", "Bengali", "Marathi", "Punjabi",
        "Urdu", "Gujarati", "Japanese", "Korean", "Chinese", "Spanish", "French", "German", "Italian",
        "Russian", "Arabic", "Turkish", "Portuguese",
    ];

    public static Result Parse(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        name = SitePrefixRx().Replace(name, "");
        name = BracketPrefixRx().Replace(name, "");

        var s = Regex.Replace(name, @"[._]+", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();

        string? quality = null;
        var q = QualityRx().Match(s);
        if (q.Success)
        {
            var v = q.Value.ToLowerInvariant();
            quality = v is "2160p" or "4k" or "uhd" ? "4K" : v;
        }

        int? year = null;
        string? title = null;
        foreach (Match m in YearRx().Matches(s))
        {
            var before = CleanTitle(s[..m.Index]);
            if (before.Length == 0) continue; // e.g. a film actually called "2012"
            title = before;
            year = int.Parse(m.Value, CultureInfo.InvariantCulture);
            break;
        }

        if (title is null)
        {
            var junk = JunkRx().Match(s);
            title = junk.Success && junk.Index > 0 ? CleanTitle(s[..junk.Index]) : CleanTitle(s);
        }
        if (title.Length == 0) title = Path.GetFileNameWithoutExtension(fileName);

        var languages = KnownLanguages
            .Select(l => (lang: l, idx: s.IndexOf(l, StringComparison.OrdinalIgnoreCase)))
            .Where(x => x.idx >= 0)
            .OrderBy(x => x.idx)
            .Select(x => x.lang)
            .ToList();

        return new Result(title, year, quality, languages);
    }

    private static string CleanTitle(string s) => s.Trim().TrimEnd('(', '[', '-', ' ', '{').Trim();
}

/// <summary>Maps ISO 639 codes from media tracks ("hin", "tel", "en") to display names.</summary>
public static class LanguageNames
{
    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    // ISO 639-2/B codes that differ from the /T codes .NET cultures use.
    private static readonly Dictionary<string, string> BibliographicCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fre"] = "French", ["ger"] = "German", ["chi"] = "Chinese", ["dut"] = "Dutch", ["gre"] = "Greek",
        ["per"] = "Persian", ["cze"] = "Czech", ["rum"] = "Romanian", ["slo"] = "Slovak", ["bur"] = "Burmese",
    };

    public static string? FromCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        code = code.Trim();
        if (code.Equals("und", StringComparison.OrdinalIgnoreCase) || code.Equals("mis", StringComparison.OrdinalIgnoreCase))
            return null;

        lock (Cache)
        {
            if (Cache.TryGetValue(code, out var cached)) return cached;
            var name = Lookup(code);
            Cache[code] = name;
            return name;
        }
    }

    private static string Lookup(string code)
    {
        if (BibliographicCodes.TryGetValue(code, out var b)) return b;
        var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(c =>
            c.ThreeLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase)
            || c.TwoLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (culture is not null && culture.EnglishName is { Length: > 0 } en && !en.StartsWith("Unknown"))
            return en.Split(' ')[0];
        // Not a code - probably already a name ("Hindi"); keep as is.
        return code.Length > 3 ? code : code.ToUpperInvariant();
    }
}
