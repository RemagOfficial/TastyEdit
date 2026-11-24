# TastyEdit

**Beta 0.9.0** — a native Windows level editor for the Tasty Planet games, written in C#
(.NET 8 + WPF, no XAML). One program opens all four builds, shows them the way the engine
shows them, and saves them back in their own binary format.

| Family | Game | Levels live in |
|---|---|---|
| `tp2` | Tasty Planet 2: Back for Seconds | `assets/levels/*.bin` |
| `tb` | Tasty Blue | `assets/editabledirect/levels/*.bin` |
| `f4` | Tasty Planet Forever | `assets/editabledirect/levels/*.bin` |
| `f5` | Tasty Planet Final Bite | `assets/editabledirect/levels/*.bin` |

The game is detected from the file, so it is just *Open level* for all four.

## What you can edit

Everything the level file carries that the engine itself exposes: walls, defined areas,
paths, decorations, water records and entities are drawn from the games' own sprite tables,
and each of them can be moved, rotated, re-pointed at another sprite, copied, pasted,
duplicated, deleted, moved to another level, and undone/redone step by step. New entities
can be added, and their type is checked against the definitions the shipped game actually
has (an unknown type parses and then gets dropped in silence by the loader, so the editor
refuses it rather than writing a level that loses the object in game).

The inspector shows the real fields of whatever is selected — position, rotation, facing
(**Flip X / Flip Y**), colour and alpha, sprite, dimensions, level, and the format-specific
ones: water behaviour, TP2 entity fields, multilevel/goal settings and the entity
behaviour tail. Fields that a record does not have are not faked; nothing is written that
the codec cannot prove it can read back.

## Requirements

- Windows x64 and the **.NET 8 Desktop Runtime** (or the .NET 8 SDK to build).
- A legally-owned install of at least one of the games. TastyEdit ships **no** game files,
  art or levels: it reads the artwork out of the install the level came from, which is also
  how it knows what each object should look like.

## Build and run

```
dotnet build TastyLevelEditor.sln
dotnet run --project src/TastyEditor.App
```

The executable is `src/TastyEditor.App/bin/Debug/net8.0-windows/TastyEdit.exe` — pass a level
path to open it straight away. For a distributable build:

```
dotnet publish src/TastyEditor.App -c Release -r win-x64 --self-contained false
```

## Getting around

| Input | Action |
|---|---|
| Left click / drag | select, then drag the selected element |
| **Middle mouse drag** | pan the view (from anywhere; it never grabs an element) |
| Right click | context menu for whatever is under the cursor |
| Wheel | zoom |
| `Ctrl+Z` / `Ctrl+Y` | undo / redo (each edit and each drag is its own step) |
| `View ▸ Fit` | frame the whole level |
| `View ▸ Always draw sprites` | draw sprites even when they are a few pixels on screen (off = the faster marker view for very large levels) |

`File ▸ Register .bin association` is per-user, reversible from the same menu, and never
touches anything but your own HKCU registry keys.

## Beta status — what to expect

This is a beta: it is safe on the files it claims to support, and it is honest about the rest.

- **Always make a copy of a level before saving over it.** Saving in place writes into your
  game folder; nothing in the editor can restore it for you.
- Unedited levels save **byte-identical**, and an edit only touches the record it changed —
  both are machine-checked, see below.
- Not yet in: creating a level from scratch, the four clip-rectangle integers a decoration
  record can carry (they are preserved through every edit, just not editable), and per-object
  render ordering of wall/area/water outlines against markers.
- Sprites need the game's `assets` tree next to the level; without it the objects still load,
  edit and save, and draw as position markers instead.
- The app icon is a placeholder. `assets/app-icon.png` is the source artwork and
  `python tools/make_icon.py` regenerates `src/TastyEditor.App/app.ico` from it, so swapping
  the art later is a one-file change.

## How it stays honest

The format rules were reverse-engineered from the games' own code and data, and every rule in
the codecs has a headless proof behind it. The same binary that runs the UI runs the gates:

```
dotnet run --project src/TastyEditor.App --no-build -- <gate>
```

`savecheck` `flips` `decofit` `editops` `decoedit` `decodops` `entops` `entityedit`
`entitynew` `entitydefs` `entitytypes` `moveops` `undo` `refill` `tp2fields` `tp2reassign`
`fields` `tailedit` `sprites` `smoke`

Each ends in a `GREEN`/`RED` line. They check the two invariants the whole editor rests on:
**I1** — a level that is opened and saved without edits comes back byte-identical; **I3** — an
edit changes the bytes of the record it touched and no others. Edits that change a record's
length (adding a decoration's facing block, renaming an entity) are re-decoded, re-projected
and restored to the original bytes exactly by undo.

The codec corpus test replays stored golden records against the codecs:

```
dotnet run --project tests/TastyEditor.Core.Tests --no-build -- tb f4 f5 tp2
```

It needs the golden data (generated from the level files) and reports `skipping` when it is
absent.

## Repository layout

```
src/TastyEditor.Core/   formats: codecs, models, the scene projection, every write primitive
src/TastyEditor.App/    the WPF shell: viewport, inspector, layers dock, asset browser, headless gates
tests/                  codec corpus harness (console, no package dependencies)
tools/make_icon.py      app.ico from assets/app-icon.png
```

`TastyEditor.App` is the only project that knows about windows and pixels; `TastyEditor.Core`
holds all format knowledge and never references the UI. The build is fully offline: no NuGet
packages are required (`global.json` pins the SDK so the cached WindowsDesktop targeting packs
are used).

## Credits

- The first pass at the TP2 format came from the community editor by RemagOfficial:
  <https://github.com/RemagOfficial/Tasty-Planet-2-level-editor>
- Format research for all four games was done against the shipped games' own code and data.

TastyEdit is a fan tool for games you own. It contains no game content and is not affiliated
with or endorsed by the game's rights holder.

## License

GPL-3.0, see [LICENSE](LICENSE).

Copyright © 2026 RemagOfficial

TastyEdit is free software: you can redistribute it and/or modify it under the terms of the GNU
General Public License as published by the Free Software Foundation, either version 3 of the
License, or (at your option) any later version. It is distributed in the hope that it will be
useful, but without any warranty; without even the implied warranty of merchantability or
fitness for a particular purpose — which is also a fair summary of the advice in this file:
**back up your level files before you save over them.**
