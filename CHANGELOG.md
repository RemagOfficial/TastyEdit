# Changelog

All notable changes to TastyEdit are documented here. The newest release is at the top.

## 0.9.0-beta — 24 November 2026

First public beta, released on the 20th anniversary of the original Tasty Planet.

### What it does

- **Four games in one editor**: Tasty Planet 2: Back for Seconds, Tasty Blue, Tasty Planet Forever
  and Tasty Planet Final Bite. Open any level `.bin` and the game is detected automatically.
- **See the real level.** Objects are drawn with your own install's artwork, not placeholders, and
  the whole thing stays smooth on levels with thousands of objects.
- **Change where things are**: drag them, or type exact X / Y coordinates.
- **Change how they face**: rotation, plus the flip left/right and flip up/down switches the games
  use for mirrored decorations.
- **Change how they look**: pick a different picture from the game's own artwork, and set size,
  colour and transparency.
- **Change how they behave**: walls, areas, water and moving objects expose their own settings —
  what they do to the player, what they create, how far they grow. Object types are offered only
  from the list your game actually defines, because an unknown one is silently dropped by the game.
- **Add and remove**: copy, paste, duplicate, delete, and place brand-new entities.
- **Reshape the geometry**: drag a corner of a wall or area to change it, double-click an edge to
  add a corner, drag inside to move the whole shape.
- **Compare levels**: lay a second level faintly behind the one you're editing, and see the file's
  goal structure.
- **Undo everything**: every change, including a drag, is exactly one `Ctrl+Z` step.
- **Pan without accidents**: hold the middle mouse button and drag to move the view, so left-drag
  stays reserved for moving objects.
- **Optional**: register `.bin` files so double-clicking a level in Windows opens it here.

### New in this build since the earlier private versions

- `View ▸ Always draw sprites` — objects keep their real pictures at any zoom instead of turning
  into tiny crosses, with the old lighter behaviour kept as an option.
- Flip X / Flip Y are now editable on decorations in all four games, and the game's own
  create-or-drop rule for the extra facing data is respected, so undo puts the file back exactly.
- Behaviour settings are editable by name for entities, water and the per-game tails that used to
  be read-only.
- An app icon, and the version number in the title bar, so a bug report names the build.

### Known limitations

- **No level from scratch yet** — you edit copies of levels that already exist.
- Four rarely-used clip rectangle numbers inside a decoration are carried through every edit
  untouched but can't be changed yet.
- Outline objects (walls, areas, water) and markers are drawn in file order, so which one is "on
  top" can differ from the game in a few spots. Purely a display detail; saving is unaffected.
- The published `TastyEdit.exe` is unsigned, so Windows SmartScreen interrupts the first run
  (More info → Run anyway).
- This is a beta: **keep copies of your levels** before you save into a game folder.

### How it's checked

Loading and saving was verified against every level that shipped with all four games: a level you
open and save without touching comes out exactly as it went in, and an edit only changes the object
you changed. Both of those are re-checked on real levels by the editor's own test runs — see
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

### Download

**`TastyEdit-0.9.0-beta-win-x64.zip`** — unzip it into a folder and run `TastyEdit.exe`. Nothing to
install and no .NET prerequisite; 64-bit Windows only. It contains no game files, so you need to own
the games.

### License

GNU GPL v3. Copyright © 2026 RemagOfficial. Thanks to LazyRares and Sharkie, whose earlier editor
work this builds on — see the [README](README.md).
