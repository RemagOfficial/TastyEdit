# Developing TastyEdit

Everything a contributor needs; the [README](../README.md) is deliberately user-facing.

## Layout

```
src/TastyEditor.Core/   formats: codecs, models, the scene projection, every write primitive
src/TastyEditor.App/    the WPF shell: viewport, inspector, layers dock, asset browser, headless gates
tests/                  codec corpus harness (console app, no package dependencies)
tools/make_icon.py      rebuilds the app icon from assets/app-icon.png
```

`TastyEditor.App` is the only project that knows about windows and pixels. `TastyEditor.Core` holds
all format knowledge and never references the UI, so a level can be decoded, edited and re-encoded
with no WPF in the process — which is exactly how the gates below run.

## Build

```
dotnet build TastyLevelEditor.sln
dotnet run --project src/TastyEditor.App                 # the editor
dotnet run --project src/TastyEditor.App -- <gate>       # a headless gate
```

Requires the .NET 8 SDK. `global.json` pins the SDK version so the cached WindowsDesktop targeting
packs resolve; with a plain .NET 8 SDK installed the build needs no NuGet access at all.

### Pointing the checks at your installed games

The gates read stock levels to verify themselves. `GameInstall.LevelsDir` finds them: an explicit
`TPE_LEVELS_TP2` / `_TB` / `_F4` / `_F5` environment variable first, then the Steam libraries
listed in `libraryfolders.vdf`, then the default Program Files library. A game it can't find is
reported as `NOTE no <game> level folder found - ...` and contributes no levels, so an empty-looking
run means the path is wrong, not that the format broke.

## The two invariants

Every write primitive exists to keep these true, and both are checked by gates rather than by
trust:

- **I1 — do no harm.** A level that is opened and saved without any edit produces byte-identical
  output. Each codec therefore keeps whatever it cannot interpret, and a family is only allowed to
  save when its untouched round trip is proven.
- **I3 — stay local.** An edit changes the bytes of the record it touched and nothing else. Length
  changing edits (adding a decoration's facing block, renaming an entity) are allowed to move later
  offsets, so the gate checks the diff *relative to the shifted layout* and that undo restores the
  original bytes exactly.

When a format rule looks arbitrary in the code, it came from the game rather than from a preference.
The comments name the engine function each layout came from.

## Verification gates

The same executable that shows windows runs headless proofs:

```
dotnet run --project src/TastyEditor.App --no-build -- <gate>
```

| Gate | Proves |
|---|---|
| `savecheck` | I1: untouched levels re-encode byte-identically, and an edited file re-decodes to itself |
| `flips` | the decoration facing edit creates/drops its 24-byte block and restores the shipped bytes on undo |
| `decofit` | a decoration's world rectangle and sprite fit match the record |
| `editops` `decoedit` `decodops` `entops` `entityedit` `moveops` | field writes stay inside the touched record, per family |
| `entitynew` `entitydefs` `entitytypes` | new entities are only offered for types the shipped game defines |
| `undo` | each edit and drag is one undo step, and steps back to the loaded bytes |
| `refill` `fields` `tp2fields` `tp2reassign` `tailedit` | water, per-family field, sprite reassignment and behaviour-tail edits |
| `sprites` `smoke` | the asset bridge and a general load/edit sweep |

Every gate ends in a `GREEN` or `RED` line and writes a `_flips.txt`-style log in the working
folder (ignored by git). Add a gate alongside any new write primitive — that is the bar for merging.

## Codec corpus test

Replays stored golden records against the codecs:

```
dotnet run --project tests/TastyEditor.Core.Tests --no-build -- tb f4 f5 tp2
```

It expects golden data and the installed games; with neither present it reports `skipping` rather
than failing, which is why it does not run in a fresh clone. Set `GOLDENS_DIR` to a golden tree to
run it.

By default it samples six goldens per family, picked by **stride** through the name-sorted list —
the first six f4 levels are all `aircon*`, and what a sample is supposed to buy is the family's
variety, not its sort order — so the run takes seconds instead of the tens of minutes a full replay
costs (the Forever / Final Bite levels run to megabytes and the entity-block search is super-linear
in them). `--all` replays every golden. Levels that have been framed wrongly at some point are kept
in a `MustRun` list and replay whatever the sample does, so a sampled run is still a gate rather
than a spot check; the summary line says how many goldens were not replayed.

The number that matters is `raw-span framing authority: N record-exact at both ends, …, 0 lenient`:
which of the decoder's passes produced the answer, not only whether the answer round-trips. A
byte-identical round-trip cannot tell the passes apart, because the opaque entity block re-emits
whatever the walk sliced out of the file. A lenient pick fails the run.

The goldens record what the earlier Python tooling read, and that is **not** an authority on these
formats — the shipped bytes are. Where a golden's raw-span answer and the C# answer differ, the run
reports it as advisory and stays green; the fix is to settle it against the file.

When a level will not frame: `-- ents f4 <file-or-dir>` reports, per entity section, whether the
record split succeeds and if not the first record the grammar cannot measure, with its bytes; and
`TPE_DECODE_DIAG=1` narrates every strict-pass decline and every unanchored section to stderr.

## Icons

`assets/app-icon.png` is the source artwork for the whole set.

```
python tools/make_icon.py            # writes src/TastyEditor.App/app.ico
python tools/make_icon.py --games    # writes src/TastyEditor.App/icons/<key>.ico, one per game
```

Each emits a 10-entry icon (16/20/24/32/40/48/64/96/128/256) so the small sizes are rendered from
the source rather than downscaled from one big bitmap. The project embeds `app.ico` with
`<ApplicationIcon>` (Explorer, taskbar, alt-tab) and carries it as a `<Resource>` for the window
title bar.

The five family tiles are `app.ico` with its purple re-lit in the game's accent colour (the same RGB
the status-bar chip uses in `MainWindow.GameChip`) and its "TE" replaced by the family code. They
ship as files next to the executable (`<Content>` in the project), because two things need to read
them from disk rather than out of the binary: the window swaps `Icon` to the open level's family
(`GameIcons`), and the installer points each Start-Menu shortcut at one — Explorer will not take an
icon out of the apphost for a shortcut. A shared `.bin` association can only ever carry one icon, so
that one stays the application's.

## Making a release

1. Set `<Version>` in `src/TastyEditor.App/TastyEditor.App.csproj`, add an entry for it at the top
   of [CHANGELOG.md](../CHANGELOG.md), and commit **before** building the artifact — the SDK stamps
   `InformationalVersion` with the repo commit, and that is how a bug report gets traced to a build.
2. Publish the self-contained build players download (no .NET install needed):
   ```
   dotnet publish src/TastyEditor.App -c Release -r win-x64 --self-contained true -t:Rebuild -o publish/TastyEdit-<version>
   ```
3. Zip the folder one level above the repo so it can't be committed:
   `tar.exe -a -cf ..\TastyEdit-<version>-win-x64.zip -C publish TastyEdit-<version>`
4. Build the installer from that same publish folder, with [Inno Setup](https://jrsoftware.org/isinfo.php)
   7 or newer installed. The version is passed in, so one number drives the artifact name, the
   publish folder it reads and the Start-Menu entry:
   ```
   & "C:\Program Files\Inno Setup 7\ISCC.exe" "/DMyAppVersion=<version>" installer\TastyEdit.iss
   ```
   It writes `installer\Output\TastyEdit-Setup-<version>.exe` (that folder is gitignored). If the
   version isn't passed, the script refuses to compile rather than guess one.
5. `git tag -a v<version>` on the release commit, then push the branch **and the tags**
   (`git push origin main --follow-tags`).
6. Create the release on GitHub pointing at that tag, attach **both** the zip and the installer, and
   paste the CHANGELOG entry as the release notes.

A docs-only commit still needs a re-publish before the artifact ships, and `dotnet publish` is
incremental: pass `-t:Rebuild`, or the copied exe keeps the previous commit's stamp.

The published apphost is unsigned, so first run triggers Windows SmartScreen; the README tells users
about it rather than having them assume a problem.

## Not in this repository

The format research journal — how each field, offset and rule was found in the games' own code,
with the evidence behind it — is kept privately. What a contributor needs is here instead: the
layout comments in the codecs, and a gate for every write path.
