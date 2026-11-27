# The `.tpm` mod format

A TastyEdit mod is a **plain zip archive** with the extension `.tpm`. Anyone can make one with any
zip tool; TastyEdit is the recommended way, not a requirement. This document is the contract — the
fields and the folder layout below are what the editor writes and what it accepts, and they stay
stable across releases.

## The shape

```
my-mod.tpm
├── metadata.json          ← required, at the very root
└── assets/
    ├── levels/lab1a.bin
    ├── levels/lab1a.xml
    └── graphics/somecell.png
```

Everything under `assets/` is laid out **exactly as it sits inside the game's own `assets` folder**,
so `assets/levels/lab1a.bin` replaces the game's `<game>\assets\levels\lab1a.bin`. Entry names use
forward slashes, as the zip spec says.

A mod only carries the files it changes or adds. Anything it doesn't mention stays the game's own.

## `metadata.json`

```json
{
  "name": "A Second Bite",
  "author": "you",
  "version": "1.0",
  "description": "Eight levels that start where Final Bite ends.",
  "game": "f5"
}
```

| Field | Required | What it is |
|---|---|---|
| `name` | **yes** | The mod's name. It's what the mod list shows, and it's where the mod's files live on disk. |
| `game` | **yes** | Which game the mod is for: `tp1`, `tp2`, `tb`, `f4` or `f5`. Installing a mod into a different game is refused. |
| `author` | no | Credit line. |
| `version` | no | Free-form tag, e.g. `1.0`. Shown in the mod list. |
| `description` | no | One or two lines, shown in the mod list. |

Reading is forgiving on purpose: field names are matched ignoring case, comments and trailing
commas are allowed, and a field TastyEdit doesn't know about is ignored rather than rejected — so a
newer format doesn't brick an older editor.

## What TastyEdit checks before installing

1. the file opens as a zip;
2. `metadata.json` is there and parses;
3. it has a `name` and a `game` the editor knows;
4. at least one file sits under `assets/`.

An entry whose name would escape the assets folder (something like `../../documents/x`) aborts the
whole install before anything is left behind.

## What installing does

The games only ever read a folder literally named `assets`, so a mod is installed by building a
complete copy of the game's files with the mod's overlaid on top, and renaming that copy into place:

```
<game>\assets                  ← the live one; whichever folder is renamed to this is what the game plays
<game>\assets-vanilla          ← a pristine copy of the game's files, taken once and kept
<game>\assets-A-Second-Bite    ← one merged copy per installed mod, named after its `name`
```

The mod folder is the mod's name with spaces turned into dashes and anything a path wouldn't allow
replaced the same way.

Each of those folders carries a `.tpm-mod.json` marker inside it — name, game, version, and whether
it's the untouched vanilla copy — so the mod list can tell them apart after the renames. Nothing is
deleted: **Revert** is a rename back to `assets-vanilla`, and switching between mods is a rename
too. Swaps that fail partway are undone, so the game is never left without an `assets` folder.

That means a `.tpm` needs no loader and doesn't patch the game's code. It's the game's folder, with
your files in it, and a copy of the originals kept so you can go back.

## Making one by hand

1. Copy the files you changed out of the game, keeping their paths relative to its `assets` folder.
2. Put them under an `assets/` folder in a new directory, and write `metadata.json` next to it.
3. Zip the *contents* of that directory (so `metadata.json` is at the root of the zip, not inside a
   folder) and name the result `something.tpm`.

Publishing from TastyEdit (**Mod ▸ Publish mod as .tpm…**) does the same thing, plus it leaves out
any file that is still byte-identical to the game's own — a mod should carry only what it changed.
