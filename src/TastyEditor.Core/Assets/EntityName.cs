using System.Text;
using System.Text.RegularExpressions;

namespace TastyEditor.Core.Assets;

/// <summary>Name math ported from <c>tb_entities.py</c> (species_key / catalog_base /
/// <c>_BIN_SUFFIX_RE</c>) and <c>tbassets.py</c> (the alias table and the normalising
/// <c>_norm</c>). These turn a raw level spawn name into the catalog / cell spelling the
/// graphics are keyed by.</summary>
public static class EntityName
{
    // (base|lab1|orca1|plankton2|dolphin1|arctic1|beach1|garbage1|junk1|slider) suffix.
    private static readonly Regex BinSuffix = new(
        @"_(base|lab\d+|orca\d+|plankton\d+|dolphin\d+|arctic\d+|beach\d+|garbage\d+|junk\d+|slider)$",
        RegexOptions.Compiled);

    public static string CatalogBase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return name.EndsWith("_base", StringComparison.Ordinal) ? name[..^5] : name;
    }

    public static string SpeciesKey(string name)
    {
        string stem = CatalogBase(name ?? "");
        var m = BinSuffix.Match(stem);
        return m.Success ? stem[..m.Index] : stem;
    }

    /// <summary>Lowercase, strip everything outside <c>[a-z0-9]</c> — the
    /// <c>tbassets._norm</c> used for cell/anim key matching.</summary>
    public static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.ToLowerInvariant())
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
        return sb.ToString();
    }

    // tbassets.aliases: spawn spellings whose graphics use the underscored name.
    public static readonly Dictionary<string, string> Aliases = new()
    {
        ["greatwhiteshark"] = "great_white_shark",
        ["whitetipshark"] = "white_tip_shark",
        ["killerwhale"] = "orca",
        ["goldfish"] = "gold_fish",
        ["clownfish"] = "clown_fish",
        ["braincoral1"] = "brain_coral_1",
        ["braincoral2"] = "brain_coral_2",
        ["sunkenpiratesship"] = "sunken_pirates_ship",
        ["attackhelicopter"] = "attack_helicopter",
        ["aircraftcarrier"] = "aircraft_carrier",
        ["personalsubmarine"] = "personal_submarine",
        ["underwatercar"] = "underwater_car",
        ["jumbosquid"] = "jumbo_squid",
        ["phytoplankton"] = "phyto_plankton",
        ["zooplankton"] = "zoo_plankton",
    };

    /// <summary>Ratcliff/Obershelp similarity (Python <c>difflib.SequenceMatcher.ratio</c>)
    /// for the short keys here: <c>2 * matched / (len(a)+len(b))</c>, where matched is the
    /// recursive sum of longest-common-substring blocks. No autojunk (candidates are short).</summary>
    public static double Ratio(string a, string b)
    {
        int total = a.Length + b.Length;
        if (total == 0) return 1.0;
        return 2.0 * MatchCount(a, 0, a.Length, b, 0, b.Length) / total;
    }

    private static int MatchCount(string a, int alo, int ahi, string b, int blo, int bhi)
    {
        // Longest common substring within the two ranges.
        int besti = alo, bestj = blo, bestSize = 0;
        if (ahi <= alo || bhi <= blo) return 0;
        // jrun[j] = length of common suffix ending at a[i-1]/b[j-1].
        var prev = new int[bhi];
        var cur = new int[bhi];
        for (int i = alo; i < ahi; i++)
        {
            Array.Clear(cur, 0, cur.Length);
            char ca = a[i];
            for (int j = blo; j < bhi; j++)
            {
                if (ca != b[j]) continue;
                int k = (i > alo && j > blo ? prev[j - 1] : 0) + 1;
                cur[j] = k;
                if (k > bestSize) { bestSize = k; besti = i - k + 1; bestj = j - k + 1; }
            }
            (prev, cur) = (cur, prev);
        }
        if (bestSize == 0) return 0;
        int left = MatchCount(a, alo, besti, b, blo, bestj);
        int right = MatchCount(a, besti + bestSize, ahi, b, bestj + bestSize, bhi);
        return bestSize + left + right;
    }

    /// <summary>The best candidate whose similarity to <paramref name="q"/> is at least
    /// <paramref name="cutoff"/>, or null — the single result <c>tbassets._fuzzy_cell</c>
    /// actually consumes from <c>difflib.get_close_matches</c>.</summary>
    public static string? BestClose(string q, IEnumerable<string> candidates, double cutoff)
    {
        string? best = null; double bestR = cutoff;
        foreach (var c in candidates)
        {
            double r = Ratio(q, c);
            if (r > bestR) { bestR = r; best = c; }
        }
        return best;
    }

    /// <summary>Up to <paramref name="n"/> candidates with <c>Ratio(q, c) &gt;= cutoff</c>,
    /// best-first — a faithful stand-in for Python <c>difflib.get_close_matches</c> (which
    /// keeps <c>&gt;= cutoff</c> then <c>heapq.nlargest</c> by <c>(score, name)</c>). TP2's
    /// <c>_fuzzy_cell</c> walks these in order and returns the first passing its trailing-
    /// digit guard, so the full ranked list (not just the top hit) is needed here.</summary>
    public static List<string> CloseMatches(string q, IEnumerable<string> candidates, int n, double cutoff)
    {
        var scored = new List<(double Score, string Name)>();
        foreach (var c in candidates)
        {
            double r = Ratio(q, c);
            if (r >= cutoff) scored.Add((r, c));
        }
        // nlargest on (score, name): score descending, ties by name descending (ordinal).
        return scored
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Name, StringComparer.Ordinal)
            .Take(n)
            .Select(x => x.Name)
            .ToList();
    }
}
