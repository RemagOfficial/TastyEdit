using System.Security.Cryptography;
using System.Text.Json;
using TastyEditor.Core.Levels;

// TastyEditor.Core golden-corpus runner (ROADMAP Phase D slice 1 acceptance gate).
//
// Replays every stored golden (Phase C format, produced by level_codec.analyse)
// against the C# codecs and fails on any drift. Per level it checks exactly what
// the Python _check_goldens.py gates on:
//   * source bytes unchanged   (sha256 == golden.in_sha256)
//   * I1 byte-identical round-trip (decode -> encode == source; out sha matches)
//   * structural span model equal   (section counts + every record byte length)
//   * I3 record-span edit localised (a byte-flip touches only that record)
// (semantic-model JSON equality is a Python-internal check; byte-identical I1
//  plus the span model is the stronger, cross-language contract we honour here.)
//
// usage: dotnet run -- [f5 f4 tb tp2 ...]     GOLDENS_DIR / TPE_LEVELS_<game> override.

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: golden-runner <game> [game ...]");
    return 2;
}

string root = Directory.GetCurrentDirectory();
string goldensDir = Environment.GetEnvironmentVariable("GOLDENS_DIR")
                    ?? Path.Combine(root, "goldens_sub");

// Where each game's stock .bin files are, found on this machine rather than assumed: an explicit
// TPE_LEVELS_<game> wins, otherwise the Steam libraries are searched (see GameInstall).
var levelsDir = new Dictionary<string, string>();
foreach (var game in new[] { "tp2", "tb", "f4", "f5" })
{
    var found = GameInstall.LevelsDir(game);
    levelsDir[game] = found ?? "";
    if (found is null) Console.Error.WriteLine("NOTE " + GameInstall.MissingHint(game));
}

// The codecs ported so far. Games not yet in the dictionary are reported but do
// not fail the run, so the harness is usable incrementally across slice 1.
var ported = new HashSet<string> { "f5", "f4", "tb", "tp2" };

// ---- M4 full-corpus regression mode -------------------------------------
// `dotnet run -- manifest <out.jsonl>` round-trips EVERY stock .bin through the
// C# codecs and writes one line per level: {level, game, in_sha256, out_sha256,
// ok}. A companion Python manifest (level_codec.roundtrip) is diffed against it,
// proving the C# port reproduces the oracle byte-for-byte across all 889 levels.
if (args.Length >= 2 && args[0] == "manifest")
{
    // Write straight to the file the caller names, so a PowerShell console
    // redirect can never truncate or mis-route the 889-line output.
    using var writer = new StreamWriter(args[1], append: false);
    int EmitLine(string game)
    {
        string dir = levelsDir[game];
        if (!Directory.Exists(dir)) { Console.Error.WriteLine($"missing dir {dir}"); return -1; }
        int n = 0;
        foreach (var bin in Directory.GetFiles(dir, "*.bin").OrderBy(f => f))
        {
            var data = File.ReadAllBytes(bin);
            string outSha; bool ok;
            try
            {
                var prof = GameRegistry.Profile(game);
                var model = prof.Decode(data);
                var re = prof.Codec.Encode(model);
                ok = re.AsSpan().SequenceEqual(data);
                outSha = Sha(re);
            }
            catch (Exception) { ok = false; outSha = ""; }
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                level = Path.GetFileName(bin), game,
                in_sha256 = Sha(data), out_sha256 = outSha, ok,
            }));
            n++;
        }
        return n;
    }
    int totalEmit = 0;
    foreach (var g in new[] { "f5", "f4", "tb", "tp2" }) totalEmit += EmitLine(g);
    writer.Flush();
    Console.Error.WriteLine($"emitted {totalEmit} manifest lines to {args[1]}");
    return 0;
}

// `dotnet run -- probe <game> <path.bin>` decodes/re-encodes one file and prints
// the exact exception + stack if the codec throws (M4 triage helper).
if (args.Length >= 3 && args[0] == "probe")
{
    var pg = args[1];
    var pdata = File.ReadAllBytes(args[2]);
    try
    {
        var pprof = GameRegistry.Profile(pg);
        var pmodel = pprof.Decode(pdata);
        var prel = pprof.Codec.Encode(pmodel);
        Console.WriteLine($"probe {pg}: ok={prel.AsSpan().SequenceEqual(pdata)} len={prel.Length}/{pdata.Length}");
    }
    catch (Exception pex)
    {
        Console.WriteLine($"probe {pg}: THREW {pex.GetType().Name}: {pex.Message}");
        Console.WriteLine(pex.StackTrace);
    }
    return 0;
}

int total = 0, passed = 0, notPorted = 0;
var failures = new List<string>();

foreach (string game in args)
{
    var dir = Path.Combine(goldensDir, game);
    if (!Directory.Exists(dir))
    {
        Console.WriteLine($"[{game}] no goldens at {dir}; skipping");
        continue;
    }
    if (!ported.Contains(game))
    {
        Console.WriteLine($"[{game}] codec not ported yet — skipped (not a failure)");
        notPorted++;
        continue;
    }

    string src = levelsDir[game];
    int gp = 0, gok = 0;
    foreach (var gf in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
    {
        if (Path.GetFileName(gf).StartsWith('_')) continue;
        gp++; total++;
        var gold = JsonDocument.Parse(File.ReadAllText(gf)).RootElement;
        string level = gold.GetProperty("level").GetString()!;
        string stem = Path.GetFileNameWithoutExtension(gf);
        string binPath = Path.Combine(src, level);
        if (!File.Exists(binPath)) { failures.Add($"{game}/{stem}: source .bin missing"); continue; }

        var data = File.ReadAllBytes(binPath);
        try
        {
            var problems = CheckOne(game, binPath, data, gold);
            if (problems.Count == 0) { gok++; passed++; }
            else foreach (var p in problems) failures.Add($"{game}/{stem}: {p}");
        }
        catch (Exception ex)
        {
            failures.Add($"{game}/{stem}: EXCEPTION {ex.GetType().Name}: {ex.Message}");
        }
    }
    Console.WriteLine($"[{game}] checked {gp} goldens  OK={gok}  FAIL={gp - gok}");
}

Console.WriteLine();
Console.WriteLine($"C# GOLDEN CHECK: {passed}/{total} levels green"
                  + (notPorted > 0 ? $"  ({notPorted} games pending port)" : ""));
foreach (var f in failures.Take(40)) Console.WriteLine("   FAIL " + f);
Console.WriteLine(failures.Count == 0 && total > 0 ? "CORE CODEC GREEN" : "CORE CODEC RED");
return failures.Count == 0 && total > 0 ? 0 : 1;

static List<string> CheckOne(string game, string binPath, byte[] data, JsonElement gold)
{
    var problems = new List<string>();
    string inSha = gold.GetProperty("in_sha256").GetString()!;
    string outSha = gold.GetProperty("out_sha256").GetString()!;

    if (Sha(data) != inSha) { problems.Add("source changed (in sha mismatch)"); return problems; }

    // Detector: the path marker must name this game, and this game's codec must
    // content-accept the file (GameRegistry mirrors games.py detect_level).
    if (GameRegistry.PathHint(binPath) != game)
        problems.Add($"path hint detected {GameRegistry.PathHint(binPath) ?? "none"}, expected {game}");
    if (!GameRegistry.MatchKeys(data).Contains(game))
        problems.Add("detector: codec did not content-accept its own golden");

    ElementLevel? el = null;
    TbLevel? tb = null;
    byte[] re;
    JsonElement cSharpSpan;

    // Decode once; the edit proof mutates the model, so run it last.
    if (game == "tb")
    {
        var codec = TbCodec.Instance;
        tb = codec.Decode(data);
        re = codec.Encode(tb);
        var spanNode = codec.SpanModel(tb);
        cSharpSpan = JsonDocument.Parse(spanNode.ToJsonString()).RootElement;
    }
    else if (game == "tp2")
    {
        var codec = Tp2Codec.Instance;
        var tp = codec.Decode(data);
        re = codec.Encode(tp);
        var spanNode = codec.SpanModel(tp);
        cSharpSpan = JsonDocument.Parse(spanNode.ToJsonString()).RootElement;
    }
    else
    {
        var codec = ElementSpanCodec.For(game);
        el = codec.Decode(data);
        re = codec.Encode(el);
        var spanNode = codec.SpanModel(el);
        cSharpSpan = JsonDocument.Parse(spanNode.ToJsonString()).RootElement;
    }

    if (!re.AsSpan().SequenceEqual(data))
        problems.Add($"I1 not byte-identical ({re.Length} vs {data.Length} bytes)");
    else if (Sha(re) != outSha)
        problems.Add("out sha mismatch despite equal length");

    var goldSpan = gold.GetProperty("span_model");
    if (!JsonEq(cSharpSpan, goldSpan))
        problems.Add("span model mismatch: " + FirstDiff(cSharpSpan, goldSpan));

    if (gold.TryGetProperty("edit", out var ge) && ge.ValueKind == JsonValueKind.Object)
    {
        var editNode = game == "tb" ? TbCodec.Instance.EditProof(tb!, data)
                                    : el is not null ? ElementSpanCodec.For(game).EditProof(el, data)
                                    : null;
        if (editNode is null) problems.Add("no I3 edit produced");
        else
        {
            var cSharpEdit = JsonDocument.Parse(editNode.ToJsonString()).RootElement;
            if (!JsonEq(cSharpEdit, ge))
                problems.Add("I3 edit proof differs: " + FirstDiff(cSharpEdit, ge));
        }
    }
    return problems;
}

static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

// Order-insensitive object / order-sensitive array deep equality over JsonElement,
// comparing numbers by value (int vs float tolerant). Returns the first mismatch.
static bool JsonEq(JsonElement a, JsonElement b) => FirstDiff(a, b) is null;

static string? FirstDiff(JsonElement a, JsonElement b, string path = "")
{
    if (a.ValueKind != b.ValueKind)
    {
        // a number and a bool etc. differ; but treat equal-kind below
        return $"{path}: kind {a.ValueKind} vs {b.ValueKind}";
    }
    switch (a.ValueKind)
    {
        case JsonValueKind.Object:
            foreach (var p in a.EnumerateObject())
            {
                if (!b.TryGetProperty(p.Name, out var bp))
                    return $"{path}.{p.Name}: missing in other";
                var d = FirstDiff(p.Value, bp, $"{path}.{p.Name}");
                if (d != null) return d;
            }
            foreach (var p in b.EnumerateObject())
                if (!a.TryGetProperty(p.Name, out _))
                    return $"{path}.{p.Name}: extra in other";
            return null;
        case JsonValueKind.Array:
            if (a.GetArrayLength() != b.GetArrayLength())
                return $"{path}: len {a.GetArrayLength()} vs {b.GetArrayLength()}";
            using (var ea = a.EnumerateArray())
            using (var eb = b.EnumerateArray())
                for (int i = 0; ea.MoveNext() && eb.MoveNext();)
                {
                    var d = FirstDiff(ea.Current, eb.Current, $"{path}[{i}]");
                    if (d != null) return d;
                }
            return null;
        case JsonValueKind.Number:
            return a.GetDouble() == b.GetDouble()
                ? null : $"{path}: {a.GetRawText()} vs {b.GetRawText()}";
        case JsonValueKind.String:
            return a.GetString() == b.GetString()
                ? null : $"{path}: \"{a.GetString()}\" vs \"{b.GetString()}\"";
        case JsonValueKind.True:
        case JsonValueKind.False:
            return a.GetBoolean() == b.GetBoolean()
                ? null : $"{path}: bool {a.GetBoolean()} vs {b.GetBoolean()}";
        case JsonValueKind.Null:
            return null;
        default:
            return a.GetRawText() == b.GetRawText() ? null : $"{path}: raw differ";
    }
}
