using System.IO;

namespace TastyEditor.Core.Assets;

/// <summary>The engine's own entity → art table, read out of <c>tastyblue.exe</c>.
///
/// Tasty Blue ships no data file that says which sprite an entity TYPE draws: its
/// <c>animationdefs.xml</c> and <c>leveleditingdata.xml</c> are empty stubs, <c>entityintersections.xml</c>
/// carries only physics footprints (and does not even list names that DO render, such as
/// <c>crab_normal</c>), and the level record itself holds no art string. The binding is compiled in.
/// Each entity registration in <c>.text</c> pushes its literals in a fixed order —
/// <c>&lt;name&gt;_base</c>, a <c>&lt;species&gt;_disp</c> token, the eat sound, zero to two RRGGBB tints and
/// finally the art name — so walking the literal references in code order and cutting a record at every
/// <c>_base</c> recovers the pair. Within a record the art is the LAST literal that is an image-map cell or
/// a skeleton rig, which is what makes one rule cover both shapes: creatures name their rig
/// (<c>crab_base → crab</c>), props name a cell (<c>moon_base → planet_rockey</c>).
///
/// Pool adjacency cannot be used instead: the linker merges duplicate literals, so
/// <c>woodencratesquare_base</c> and <c>outercore1_base</c> have empty string groups, and records that do
/// not end in <c>_base</c> (<c>torpedo_kill</c>, <c>player</c>) bleed into the previous one. Only the
/// <c>.text</c> sites are unambiguous. Registration runs more than once for some entities, so identical
/// records vote and the plurality wins.
///
/// Pure bytes — no pixels, no WPF — so the gate can diff it against the Python extractor.</summary>
public sealed partial class SpriteResolver
{
    /// <summary>Record window in bytes of code. Measured over the shipped exe: every real art literal sits
    /// within 810 B of its <c>_base</c>, while the next registration starts beyond that, so 1100 is wide
    /// enough for the longest record and narrow enough to stop a bleed.</summary>
    private const uint EngineRecordWindow = 1100;

    /// <summary><c>&lt;name&gt;_base</c> → the cell or rig name the engine assembles that entity from.
    /// Null (not empty) when the install has no readable exe, so the resolver degrades to the XML cascade.</summary>
    public IReadOnlyDictionary<string, string>? EngineArt { get; init; }

    public int EngineArtCount => EngineArt?.Count ?? 0;

    /// <summary>Ask the engine table what art <paramref name="rawName"/> is built from. The table is keyed
    /// by the registration name plus <c>_base</c>, and <see cref="EntityName.SpeciesKey"/> is NOT the way to
    /// reach it — its suffix strip only covers the <c>base|labN|orcaN|…</c> family, so
    /// <c>torpedo_torpedo1</c> and <c>seaweed_normal</c> come back unchanged. Candidates, most specific
    /// first: the whole TYPE plus <c>_base</c>, then every underscore prefix longest-first (a TYPE such as
    /// <c>treasurechest_close_static</c> registers as <c>treasurechest_close_base</c>, so taking only the
    /// leading token would land on the absent <c>treasurechest_base</c>), then the species key plus
    /// <c>_base</c>, then the bare name.</summary>
    public bool TryGetEngineArt(string rawName, out string art)
    {
        art = string.Empty;
        var tbl = EngineArt;
        if (tbl is not { Count: > 0 } || string.IsNullOrEmpty(rawName)) return false;

        string whole = rawName.EndsWith("_base", StringComparison.Ordinal) ? rawName : rawName + "_base";
        if (tbl.TryGetValue(whole, out var exact)) { art = exact; return true; }
        for (int i = rawName.LastIndexOf('_'); i > 0; i = rawName.LastIndexOf('_', i - 1))
            if (tbl.TryGetValue(rawName[..i] + "_base", out var hit)) { art = hit; return true; }
        if (tbl.TryGetValue(EntityName.SpeciesKey(rawName) + "_base", out var sp)) { art = sp; return true; }
        if (tbl.TryGetValue(rawName, out var bare)) { art = bare; return true; }
        return false;
    }

    /// <summary>Scanned tables by executable path, with the file's length and write time so a patched
    /// binary is re-read. The scan is a pass over the whole <c>.text</c> section (3 MB), and a resolver is
    /// built on every level load, so the one table that never changes during a session is worth memoing.</summary>
    private static readonly Dictionary<string, (long Len, DateTime Written, Dictionary<string, string>? Table)>
        _engineMemo = new();

    /// <summary>Load the registration table for one install, from the memo when the binary is unchanged.
    /// Returns null for anything that is not a readable PE (absent file, packed section, non-Windows
    /// build) rather than throwing: this table is a bonus on top of the XML cascade, never a
    /// requirement. A copy is handed out so a caller cannot write into the shared memo.</summary>
    internal static Dictionary<string, string>? ReadEngineArt(string exePath,
        IReadOnlyCollection<string> cellNames, IReadOnlyCollection<string> rigNames)
    {
        try
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return null;
            var info = new FileInfo(exePath);
            lock (_engineMemo)
            {
                if (_engineMemo.TryGetValue(exePath, out var memo)
                    && memo.Len == info.Length && memo.Written == info.LastWriteTimeUtc)
                    return Clone(memo.Table);
            }
            var table = ScanEngineArt(File.ReadAllBytes(exePath), cellNames, rigNames);
            lock (_engineMemo) _engineMemo[exePath] = (info.Length, info.LastWriteTimeUtc, table);
            return Clone(table);

            static Dictionary<string, string>? Clone(Dictionary<string, string>? t) =>
                t is null ? null : new Dictionary<string, string>(t, StringComparer.Ordinal);
        }
        catch (Exception) { return null; }
    }

    /// <summary>The ElementEngine installs keep the game binary next to the <c>assets</c> folder and it
    /// is the only carrier of the entity → art table. Rather than hardcode one title's exe name, walk
    /// the directory's <c>*.exe</c> biggest-first (the game binary dwarfs any launcher or uninstaller
    /// beside it) and return the first that scans. Absent directory or all-null scans return null.</summary>
    internal static Dictionary<string, string>? ReadEngineArtForDir(
        string gameDir, IReadOnlyCollection<string> cellNames, IReadOnlyCollection<string> rigNames)
    {
        try
        {
            if (!Directory.Exists(gameDir)) return null;
            foreach (var exe in new DirectoryInfo(gameDir).GetFiles("*.exe")
                         .OrderByDescending(f => f.Length))
            {
                var table = ReadEngineArt(exe.FullName, cellNames, rigNames);
                if (table is { Count: > 0 }) return table;
            }
        }
        catch (Exception) { /* unreadable directory: the XML cascade stands alone */ }
        return null;
    }

    /// <summary>The scan itself: parse the section headers, collect the <c>.rdata</c> literal pool, find
    /// every <c>.text</c> site that references one, cut records at each <c>_base</c> and vote the plurality
    /// art per name. Only the two sections it needs are parsed.
    ///
    /// Two reference forms, chosen by PE machine: Tasty Blue and Forever are PE32 x86 and push the
    /// literal's absolute address as an immediate; Final Bite is PE32+ x64, where code carries no
    /// absolute addresses at all and the compiler emits <c>REX.W 8D <modrm mod=00 rm=101> disp32</c>
    /// (a 7-byte RIP-relative <c>lea</c>) instead. Probed over <c>tastyplanet5.exe</c>: 7,965 lea sites
    /// land on asset literals and 3,145 of them on <c>*_base</c> names, while an absolute-imm32 scan of
    /// the same section finds zero — the same registration idiom, just expressed in the x64 form.</summary>
    private static Dictionary<string, string>? ScanEngineArt(byte[] blob,
        IReadOnlyCollection<string> cellNames, IReadOnlyCollection<string> rigNames)
    {
        if (blob.Length < 0x100) return null;
        if (blob[0] != 'M' || blob[1] != 'Z') return null;

        int pe = BitConverter.ToInt32(blob, 0x3C);
        if (pe < 0 || pe + 240 > blob.Length) return null;
        if (BitConverter.ToUInt16(blob, pe) != 0x4550) return null;               // "PE\0\0"
        ushort machine = BitConverter.ToUInt16(blob, pe + 4);
        bool x64 = machine == 0x8664;
        if (machine != 0x14c && !x64) return null;                                // PE32 (x86) or PE32+ (x64)
        ushort nsec = BitConverter.ToUInt16(blob, pe + 6);
        ushort optSize = BitConverter.ToUInt16(blob, pe + 20);
        // OptionalHeader.ImageBase: PE + 24 for the standard header; the field sits at +28 in the PE32
        // optional header and +24 in PE32+ (which dropped BaseOfData).
        ulong imageBase = x64 ? BitConverter.ToUInt64(blob, pe + 24 + 24) : BitConverter.ToUInt32(blob, pe + 24 + 28);

        (uint va, uint size, uint raw, uint rsize) text = default, rdata = default;
        bool haveText = false, haveRdata = false;
        for (int i = 0; i < nsec; i++)
        {
            int o = pe + 24 + optSize + i * 40;
            if (o + 40 > blob.Length) break;
            string nm = System.Text.Encoding.ASCII.GetString(blob, o, 8).TrimEnd('\0');
            var sec = (BitConverter.ToUInt32(blob, o + 12), BitConverter.ToUInt32(blob, o + 8),
                       BitConverter.ToUInt32(blob, o + 20), BitConverter.ToUInt32(blob, o + 16));
            if (nm == ".text") { text = sec; haveText = true; }
            else if (nm == ".rdata") { rdata = sec; haveRdata = true; }
        }
        if (!haveText || !haveRdata) return null;

        // --- the literal pool: NUL-terminated printable runs that look like an asset spelling ---
        var litAt = new Dictionary<ulong, string>();
        int p = (int)rdata.raw, end = (int)(rdata.raw + Math.Min(rdata.rsize, (uint)(blob.Length - rdata.raw)));
        while (p < end)
        {
            byte c = blob[p];
            if (c is < 0x20 or > 0x7e) { p++; continue; }
            int j = p;
            while (j < end && blob[j] is >= 0x20 and <= 0x7e) j++;
            if (j >= end || blob[j] != 0) { p = j + 1; continue; }               // not terminated: skip
            if (IsAssetSpelling(blob, p, j))
                litAt[imageBase + rdata.va + (uint)(p - rdata.raw)] =
                    System.Text.Encoding.ASCII.GetString(blob, p, j - p);
            p = j + 1;
        }
        if (litAt.Count == 0) return null;

        // --- who references them, in code order ---
        var artOk = new HashSet<string>(cellNames, StringComparer.Ordinal);
        foreach (var r in rigNames) artOk.Add(r);

        var votes = new Dictionary<string, List<(string Art, int N)>>();
        string? curName = null, curArt = null;
        ulong curAddr = 0;
        int tstart = (int)text.raw, tend = (int)(text.raw + Math.Min(text.rsize, (uint)(blob.Length - text.raw)));

        void Close()
        {
            if (curName is null || curArt is null) return;
            if (!votes.TryGetValue(curName, out var list)) votes[curName] = list = new List<(string, int)>();
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i].Art, curArt, StringComparison.Ordinal))
                { list[i] = (curArt, list[i].N + 1); return; }
            list.Add((curArt, 1));
        }

        void Visit(ulong addr, string lit)
        {
            if (lit.EndsWith("_base", StringComparison.Ordinal))
            {
                Close();
                curName = lit; curAddr = addr; curArt = null;
            }
            else if (curName is not null && addr - curAddr <= EngineRecordWindow && artOk.Contains(lit))
                curArt = lit;                                                       // LAST art literal wins
        }

        if (x64)
        {
            // REX.W (48/4C) 8D modrm(mod=00, rm=101) disp32: a 7-byte lea whose RIP base is the REX
            // position + 7. Final Bite's registrations reference every literal this way.
            for (int q = tstart; q + 7 <= tend; q++)
            {
                if (blob[q] is not (0x48 or 0x4C) || blob[q + 1] != 0x8D || (blob[q + 2] & 0xC7) != 0x05) continue;
                int disp = BitConverter.ToInt32(blob, q + 3);
                ulong addr = imageBase + text.va + (uint)(q - text.raw);
                if (litAt.TryGetValue(addr + 7 + (ulong)disp, out var lit)) Visit(addr, lit);
            }
        }
        else
        {
            // Stepping every byte rather than every fourth matters: `push imm32` operands are aligned, but the
            // engine's own pointer tables are not, and an aligned-only scan finds a third of the references the
            // extractor this replaces measured (5816).
            for (int q = tstart; q + 4 <= tend; q++)
            {
                uint word = BitConverter.ToUInt32(blob, q);
                if (!litAt.TryGetValue(word, out var lit)) continue;
                ulong addr = imageBase + text.va + (uint)(q - text.raw);
                Visit(addr, lit);
            }
        }
        Close();

        // --- plurality per name, ties resolved by first vote seen ---
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, list) in votes)
        {
            (string Art, int N) best = default;
            foreach (var v in list)
                if (v.N > best.N) best = v;
            if (best.Art is not null) table[name] = best.Art;
        }
        return table.Count > 0 ? table : null;
    }

    /// <summary>Gate a <c>.rdata</c> run to the spellings the asset tables themselves use: a letter, then
    /// letters / digits / <c>_</c> / <c>-</c>, 2..41 characters. Everything else in the section is source
    /// paths, format strings or debug text, and reading those as art names is what produces the bogus
    /// bindings a naive scan finds.</summary>
    private static bool IsAssetSpelling(byte[] blob, int start, int end)
    {
        if (end - start is < 2 or > 41) return false;
        for (int i = start; i < end; i++)
        {
            byte c = blob[i];
            bool letter = c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z';
            bool ok = i == start ? letter
                : letter || c is >= (byte)'0' and <= (byte)'9' or (byte)'_' or (byte)'-';
            if (!ok) return false;
        }
        return true;
    }
}
