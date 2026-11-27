using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using TastyEditor.Core.Levels;

// TastyEditor.Core golden-corpus runner (ROADMAP Phase D slice 1 acceptance gate).
//
// Replays every stored golden (Phase C format, produced by level_codec.analyse) against the C#
// codecs. Per level it checks:
//   * source bytes unchanged   (sha256 == golden.in_sha256; a level edited on this PC is a skip,
//     not a failure — the golden no longer describes the file, and saying so keeps the signal clean)
//   * I1 byte-identical round-trip (decode -> encode == source; out sha matches)
//   * structural span model equal   (section counts + every record byte length)
//   * I3 record-span edit localised (a byte-flip touches only that record)
// (semantic-model JSON equality is a Python-internal check; byte-identical I1
//  plus the span model is the stronger, cross-language contract we honour here.)
//
// The Python reference is no longer treated as correct. For tb / tp2 the goldens stay a hard gate:
// those families frame from fixed record grammars, and the two decoders have agreed on every
// golden since the port, so agreement still means something. For the raw-span families (f4 / f5)
// the golden's span model and edit proof are reported as advisory only, because the reference
// reader that wrote them searches for the entity block's end and sometimes accepts a tiling that
// merely closes — it lands later section headers inside records, and I1 cannot see the difference
// since the opaque block re-emits whatever the walk sliced. What is asserted hard there is the
// framing's authority (which search pass won) and its alignment with the file's own count fields.
//
// usage: dotnet run -- [f5 f4 tb tp2 ...] [--sample=N | --all]
//        GOLDENS_DIR / TPE_LEVELS_<game> override.
//
// By default each family contributes its first DefaultSample goldens (6, which is what
// goldens_sub holds). The shipped level files in Forever and Final Bite run to megabytes and the
// entity-block search is super-linear, so a whole-corpus replay costs tens of minutes — long
// enough that it stops being a check anyone runs. `--all` opts in when the wait is worth it.

const int DefaultSample = 6;

// Flags ride alongside the game names, so the existing `tb f4 f5 tp2` invocations are unchanged.
int sample = DefaultSample;
foreach (var a in args)
{
    if (a == "--all") sample = int.MaxValue;
    else if (a.StartsWith("--sample"))
        sample = int.TryParse(a.Split('=')[1..].FirstOrDefault()?.Trim('"'), out int n) ? n : DefaultSample;
}

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
foreach (var game in new[] { "tp2", "tb", "f4", "f5", "tp1" })
{
    var found = GameInstall.LevelsDir(game);
    levelsDir[game] = found ?? "";
    if (found is null) Console.Error.WriteLine("NOTE " + GameInstall.MissingHint(game));
}

// The codecs ported so far. Games not yet in the dictionary are reported but do
// not fail the run, so the harness is usable incrementally across slice 1.
// tp1 is a verbatim-XML codec ported in R5: it joins the manifest sweep (self-consistent
// I1 across every shipped level), but has no Python golden (the user chose a direct C#
// codec), so it is never replayed against goldens/<game>/*.json — there is no such folder.
var ported = new HashSet<string> { "f5", "f4", "tb", "tp2", "tp1" };

// Goldens that are replayed even in a sampled run, by golden stem (see the sampling below).
var MustRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "mars1_4", "mars1_5", "mars1_9", "mars1_10",
};

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
        // tp1 levels are loose XML, not .bin; medaltimes.xml is not a level and must not join the manifest.
        string pattern = game == "tp1" ? "*.xml" : "*.bin";
        foreach (var bin in Directory.GetFiles(dir, pattern).OrderBy(f => f))
        {
            if (game == "tp1" && string.Equals(Path.GetFileName(bin), "medaltimes.xml",
                StringComparison.OrdinalIgnoreCase)) continue;
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
    foreach (var g in new[] { "f5", "f4", "tb", "tp2", "tp1" }) totalEmit += EmitLine(g);
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

// Diagnostic companion to `probe`: print the C# span model for one level file, so a
// `span model mismatch` corpus failure can be read as an actual framing instead of a diff
// line. Nothing in the release flow calls it; it exists because the harness's own failure
// message is the only view of the two decoders agreeing or disagreeing.
if (args.Length >= 3 && args[0] == "spans")
{
    var sg = args[1];
    var sdata = File.ReadAllBytes(args[2]);
    try
    {
        var sprof = GameRegistry.Profile(sg);
        var smodel = sprof.Decode(sdata);
        if (smodel is ElementLevel sleve)
            Console.WriteLine(JsonSerializer.Serialize(sprof.Codec.SpanModel(sleve),
                new JsonSerializerOptions { WriteIndented = true }));
        else
            Console.WriteLine($"spans {sg}: model is not an ElementLevel");
    }
    catch (Exception sex)
    {
        Console.WriteLine($"spans {sg}: THREW {sex.GetType().Name}: {sex.Message}");
    }
    return 0;
}

// Entity-block audit. The projector draws entities only when the block splits to its own count, so a
// level whose entities do not split shows decos and walls and nothing else — with no error anywhere.
// One line per entity section: does it split, and if not, where does the reading grammar first stop.
// Accepts a level file or a directory of them.
if (args.Length >= 3 && args[0] == "ents")
{
    var egame = args[1];
    var efiles = Directory.Exists(args[2])
        ? Directory.GetFiles(args[2], "*.bin").OrderBy(f => f).ToArray()
        : new[] { args[2] };
    foreach (var file in efiles)
    {
        ElementLevel el;
        try { el = (ElementLevel)GameRegistry.Profile(egame).Decode(File.ReadAllBytes(file)); }
        catch (Exception eex)
        { Console.WriteLine($"{Path.GetFileName(file)}: decode THREW {eex.GetType().Name}: {eex.Message}"); continue; }
        Console.WriteLine($"== {Path.GetFileName(file)}  framing={el.FramingAuthority}");
        for (int li = 0; li < el.Layers.Count; li++)
        {
            var secs = el.Layers[li];
            for (int si = 0; si < secs.Count; si++)
            {
                var sec = secs[si];
                if (!sec.Opaque || sec.Count <= 0 || sec.Block.Length == 0) continue;
                var (heads, ok) = EntityFraming.SplitRecords(sec.Block, 0, sec.Count, el.Game);
                if (ok) { Console.WriteLine($"  L{li} sec{si}: {sec.Count} entities, block {sec.Block.Length} B  SPLIT ok"); continue; }
                // Find the first record the grammar cannot measure, and show its bytes.
                int bad = -1, at = 0;
                for (int i = 0; i < sec.Count; i++)
                {
                    int next = EntityFraming.MeasureRecord(sec.Block, at, sec.Block.Length, el.Game);
                    if (next <= at) { bad = i; break; }
                    at = next;
                }
                var prev = bad > 0 ? heads[Math.Min(bad, heads.Count) - 1] : default;
                string hex = bad < 0 ? ""
                    : BitConverter.ToString(sec.Block.AsSpan(at, Math.Min(24, sec.Block.Length - at)).ToArray());
                Console.WriteLine($"  L{li} sec{si}: {sec.Count} entities, block {sec.Block.Length} B  "
                    + $"SPLIT FAIL at record {bad} (offset {at}, after {(bad < 1 ? "nothing" : $"'{prev.Name}'")}) "
                    + $"bytes {hex}");
            }
        }
    }
    return 0;
}

// Framing audit for the raw-span families: decode a level and read every section's count field
// back out of the file at the offset the model claims. Prints one line per level that disagrees.
if (args.Length >= 3 && args[0] == "align")
{
    var agame = args[1];
    foreach (var file in Directory.GetFiles(args[2], "*.bin").OrderBy(f => f))
    {
        var adata = File.ReadAllBytes(file);
        try
        {
            var bad = FramingAligned(adata, GameRegistry.Profile(agame).Decode(adata));
            if (bad is not null)
                Console.WriteLine($"{Path.GetFileName(file)}: {bad}");
        }
        catch (Exception aex)
        {
            Console.WriteLine($"{Path.GetFileName(file)}: THREW {aex.GetType().Name}: {aex.Message}");
        }
    }
    return 0;
}

int total = 0, passed = 0, notPorted = 0, sampled = 0;
int spanAgree = 0, spanDrift = 0, skippedStale = 0;
int framedExact = 0, framedCountOnly = 0, framedLenient = 0;
var failures = new List<string>();

foreach (string game in args)
{
    if (game.StartsWith('-')) continue;          // a flag, not a family name
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
    // Every golden, in name order, minus the harness's own `_`-prefixed sidecars.
    var goldens = Directory.GetFiles(dir, "*.json").OrderBy(f => f)
                           .Where(f => !Path.GetFileName(f).StartsWith('_'))
                           .ToArray();
    // Sampled by default: the shipped Forever / Final Bite levels run to megabytes and the
    // entity-block search is super-linear in them, so replaying all ~600 of them costs tens of
    // minutes — which is long enough that the check stops getting run, and an unused check is not
    // a gate. Stride rather than first-N: the alphabet's first six f4 levels are all `aircon*`, and
    // what a sample is supposed to buy is the family's variety, not its sort order.
    int take = Math.Min(sample, goldens.Length);
    var stride = take == goldens.Length
        ? goldens
        : Enumerable.Range(0, take).Select(i => goldens[(int)((long)i * goldens.Length / take)]).ToArray();
    // A sample is only a gate if it still covers the failures that were really found. These four
    // Forever levels are exactly that: the shape-name dispatch mis-framed them (see
    // ElementShapes.IsCircleName) and a stride of six misses all four, so they replay whatever the
    // sample does. They are small files, so the run stays in seconds.
    var chosen = stride.Concat(goldens.Where(f => MustRun.Contains(Path.GetFileNameWithoutExtension(f))))
                       .Distinct().OrderBy(f => f).ToArray();
    if (chosen.Length < goldens.Length) sampled += goldens.Length - chosen.Length;

    int gp = 0, gok = 0, gskip = 0;
    foreach (var gf in chosen)
    {
        gp++; total++;
        var gold = JsonDocument.Parse(File.ReadAllText(gf)).RootElement;
        string level = gold.GetProperty("level").GetString()!;
        string stem = Path.GetFileNameWithoutExtension(gf);
        string binPath = Path.Combine(src, level);
        if (!File.Exists(binPath)) { failures.Add($"{game}/{stem}: source .bin missing"); continue; }

        var data = File.ReadAllBytes(binPath);
        try
        {
            var problems = CheckOne(game, binPath, data, gold, ref spanAgree, ref spanDrift,
                                    ref framedExact, ref framedCountOnly, ref framedLenient, out bool skipped);
            if (skipped) { gskip++; skippedStale++; }
            else if (problems.Count == 0) { gok++; passed++; }
            else foreach (var p in problems) failures.Add($"{game}/{stem}: {p}");
        }
        catch (Exception ex)
        {
            failures.Add($"{game}/{stem}: EXCEPTION {ex.GetType().Name}: {ex.Message}");
        }
    }
    Console.WriteLine($"[{game}] checked {gp} of {goldens.Length} goldens  OK={gok}  FAIL={gp - gok - gskip}  " +
                      $"STALE={gskip}" + (gp < goldens.Length ? "  (sampled; --all replays the family)" : ""));
}

Console.WriteLine();
Console.WriteLine(sampled > 0
    ? $"NOTE sampled run: {sampled} goldens not replayed this time (default {DefaultSample} per family; "
      + "--all for the full corpus, which takes tens of minutes)"
    : "full corpus: every golden in every family listed was replayed");
Console.WriteLine($"C# GOLDEN CHECK: {passed}/{total - skippedStale} levels green"
                  + (skippedStale > 0 ? $"  ({skippedStale} goldens whose stock file has since been edited)" : "")
                  + (notPorted > 0 ? $"  ({notPorted} games pending port)" : ""));
// How the raw-span levels were actually framed, and how far that answer is from what the Python
// reference recorded. The second number is a property of the reference, not of the codec; the
// first three are the codec's own claim, checked against the file.
Console.WriteLine($"raw-span framing authority: {framedExact} record-exact at both ends, "
                  + $"{framedCountOnly} count-exact only, {framedLenient} lenient "
                  + "(a lenient pick fails the run)");
if (spanAgree + spanDrift > 0)
    Console.WriteLine($"raw-span golden drift: {spanAgree} agree / {spanDrift} differ "
                      + "(advisory: the Python reader that wrote these goldens mis-frames entity "
                      + "blocks; see the note in the file header)");
foreach (var f in failures.Take(40)) Console.WriteLine("   FAIL " + f);
Console.WriteLine(failures.Count == 0 && total > 0 ? "CORE CODEC GREEN" : "CORE CODEC RED");
return failures.Count == 0 && total > 0 ? 0 : 1;

static List<string> CheckOne(string game, string binPath, byte[] data, JsonElement gold,
                             ref int spanAgree, ref int spanDrift,
                             ref int framedExact, ref int framedCountOnly, ref int framedLenient,
                             out bool skipped)
{
    var problems = new List<string>();
    skipped = false;
    string inSha = gold.GetProperty("in_sha256").GetString()!;
    string outSha = gold.GetProperty("out_sha256").GetString()!;

    // A level whose bytes no longer match the golden is not a codec failure: the file on this PC
    // has been edited (which is what the editor is for), so the golden simply no longer describes
    // it. Failing the run over that hides real drift under a pile of local state, so it is
    // reported as a skip and counted, and the run stays honest about what it actually checked.
    if (Sha(data) != inSha) { skipped = true; return problems; }

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
    bool rawSpanFamily = game is "f4" or "f5";
    if (rawSpanFamily)
    {
        // The Python goldens are not the authority for these two families. They were written by a
        // reference reader whose EOF walk sometimes accepts a tiling that closes but is not a
        // framing — its count fields then read data bytes, which is visible in its own output (a
        // golden that reports 65 536 entities in a section) and invisible to I1, because the opaque
        // block re-emits whatever the walk sliced either way. So the golden is kept for what it can
        // actually prove — which bytes the shipped level had, and that they round-trip — and the
        // structure is asserted against the file and against the codec's own claim instead.
        var misaligned = FramingAligned(data, el!);
        if (misaligned is not null) problems.Add("framing not aligned: " + misaligned);
        switch (el!.FramingAuthority)
        {
            case FramingAuthority.RecordExactBothEnds: framedExact++; break;
            case FramingAuthority.RecordExactCount: framedCountOnly++; break;
            case FramingAuthority.Lenient: framedLenient++;
                                           problems.Add("framing came from the lenient pass"); break;
            case null: problems.Add("codec claimed no framing authority"); break;
        }
        if (JsonEq(cSharpSpan, goldSpan)) spanAgree++;
        else spanDrift++;
    }
    else if (!JsonEq(cSharpSpan, goldSpan))
        problems.Add("span model mismatch: " + FirstDiff(cSharpSpan, goldSpan));

    if (gold.TryGetProperty("edit", out var ge) && ge.ValueKind == JsonValueKind.Object)
    {
        var editNode = game == "tb" ? TbCodec.Instance.EditProof(tb!, data)
                                    : el is not null ? ElementSpanCodec.For(game).EditProof(el, data)
                                    : null;
        if (editNode is null) problems.Add("no I3 edit produced");
        else if (rawSpanFamily)
        {
            // Same reasoning as the span model: the golden's proof names a record out of the
            // reference's own mis-slice, and in at_atlantis1_1 the span it reports runs to the last
            // byte of the file. The C# proof is checked against the file instead — localised,
            // size-preserving, nothing changed outside the touched record, a real byte range —
            // which is the I3 property the release cares about, with no reference borrowed.
            var cSharpEdit = JsonDocument.Parse(editNode.ToJsonString()).RootElement;
            foreach (var field in new[] { "localised", "size_preserved" })
            {
                bool yes = cSharpEdit.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.True;
                if (!yes) problems.Add($"I3 proof: {field} not true ({v.GetRawText()})");
            }
            if (cSharpEdit.TryGetProperty("bytes_outside_span_changed", out var bos) && bos.GetInt32() != 0)
                problems.Add($"I3 proof: {bos.GetInt32()} bytes changed outside the touched record");
            if (cSharpEdit.TryGetProperty("span", out var sp) && sp.ValueKind == JsonValueKind.Array && sp.GetArrayLength() == 2)
            {
                int a = sp[0].GetInt32(), b = sp[1].GetInt32();
                if (!(0 <= a && a < b && b <= data.Length))
                    problems.Add($"I3 proof: span [{a}, {b}) is not a range of the {data.Length}-byte file");
            }
            else problems.Add("I3 proof: no span reported");
        }
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

/// <summary>Is a raw-span level's framing aligned with the file, or merely self-consistent?
/// The entity block's end is a search result, and several wrong answers still re-encode
/// byte-identically (I1) because the opaque block absorbs whatever the walk mis-sliced: the
/// sections after it are then read from the middle of a record, and their "counts" are data
/// bytes. A real framing can only start a section where the file's own count field lives, so
/// re-reading each count from the bytes is a check no tiling can fake. Returns null when every
/// count field agrees, or a one-line description of the first that does not.</summary>
static string? FramingAligned(byte[] data, object model)
{
    if (model is not ElementLevel lv) return null;      // tb / tp2 frame from fixed grammars
    int lastEnd = -1;
    for (int li = 0; li < lv.Layers.Count; li++)
        for (int si = 0; si < lv.Layers[li].Count; si++)
        {
            var sec = lv.Layers[li][si];
            int at = sec.CountOff;
            if (at < 0 || at + 4 > data.Length) return $"layer {li} sec {si}: count offset {at} is off the file";
            int onDisk = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at, 4));
            if (onDisk != sec.Count)
                return $"layer {li} sec {si}: model count {sec.Count}, file says {onDisk} @{at}";
            lastEnd = Math.Max(lastEnd, sec.EndOff);
        }
    int tail = lv.Trailing.Length;
    if (lastEnd + tail != data.Length)
        return $"sections plus trailing reach {lastEnd + tail}, file is {data.Length}";
    return null;
}

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
