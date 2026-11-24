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

Replays stored golden records against the codecs (the same contract the Python reference tooling
produced):

```
dotnet run --project tests/TastyEditor.Core.Tests --no-build -- tb f4 f5 tp2
```

It expects golden data and the installed games; with neither present it reports `skipping` rather
than failing, which is why it does not run in a fresh clone. Set `GOLDENS_DIR` to a golden tree to
run it.

## The icon

`assets/app-icon.png` is the source artwork (currently a placeholder).

```
python tools/make_icon.py            # writes src/TastyEditor.App/app.ico
```

It emits a 10-entry icon (16/20/24/32/40/48/64/96/128/256) so the small sizes are rendered from the
original rather than downscaled from one big bitmap. The project embeds it with `<ApplicationIcon>`
(Explorer, taskbar, alt-tab) and carries it as a `<Resource>` for the window title bar.

## Making a release

1. Set `<Version>` in `src/TastyEditor.App/TastyEditor.App.csproj`, and commit **before** building
   the artifact — the SDK stamps `InformationalVersion` with the repo commit, and that is how a bug
   report gets traced to a build.
2. Publish the self-contained build players download (no .NET install needed):
   ```
   dotnet publish src/TastyEditor.App -c Release -r win-x64 --self-contained true -o publish/TastyEdit-<version>
   ```
3. Zip the folder one level above the repo so it can't be committed:
   `tar.exe -a -cf ..\TastyEdit-<version>-win-x64.zip -C publish TastyEdit-<version>`
4. `git tag -a v<version>` on the release commit, then push the branch **and the tags**.

The published apphost is unsigned, so first run triggers Windows SmartScreen; the README tells users
about it rather than having them assume a problem.

## Not in this repository

The format research journal — how each field, offset and rule was found in the games' own code,
with the evidence behind it — is kept privately. What a contributor needs is here instead: the
layout comments in the codecs, and a gate for every write path.
