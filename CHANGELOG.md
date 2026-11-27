# Changelog

All notable changes to TastyEdit are documented here. The newest release is at the top.

## 1.0.1 — 27 November 2026

A display fix, found the moment somebody opened `sink1b`. Nothing about saving changed.

### Fixed in this version

- **Objects drawn as the wrong picture.** In Tasty Blue's sink levels the fishbowl and the bag of fish
  food were being drawn as fish. The editor worked out which character a name belongs to by looking at
  what it starts with, and `fishbowl_side` starts with `fish` — so it assembled the fish out of its
  skeleton parts and painted that over the bowl, at the fish's much larger size. The same thing was
  happening to the islands in Tasty Blue and Forever, and in Forever to the beer bottle, the beehive and
  the termite mound, each of which was drawn as the animal its name merely begins with.

  A character's parts are now only used when the level actually names that character. If an object has a
  picture of its own, its own picture wins, and the guess is kept back as the last resort it always was —
  which is still what the later games' run-together names such as `catfishatlantis1_7` need, so those
  keep drawing in one piece.
- **The view fits better after it.** Because those wrong characters were far bigger than the objects they
  covered, opening a level like `sink1b` and pressing fit framed the level around art that isn't there.

Everything else in 1.0.0 is unchanged: same formats, same gates, one more check added so this class of
mix-up can't come back unnoticed.

---

## 1.0.0 — 27 November 2026

The first proper release. One more game, a way to share what you make, and an installer — on top of
everything 0.9.0-beta could already do.

### New in this version

- **Tasty Planet, the original, is supported** — five games in one editor now: Tasty Planet, Tasty
  Planet 2: Back for Seconds, Tasty Blue, Tasty Planet Forever and Tasty Planet Final Bite. The
  first game keeps its levels as plain XML text rather than a `.bin`, so its level files are opened
  from `<game>\assets\levels\` like the rest of its data; everything else works the way it does for
  the other four. Its background tilemap can be painted, and its entity types edited in the game's
  own tables.
- **Mods.** `Mod ▸ Create mod…` makes a folder for your campaign outside any game install; while a
  mod is open, your saves go into it as well as where you send them. `Mod ▸ Publish mod as .tpm…`
  turns that folder into one file you can send to somebody. On the receiving end, **Manage mods**
  (the start screen) installs a `.tpm` into a game, switches between the mods you have installed
  and takes them back out — the game's own files are copied aside first, and reverting puts them
  back exactly as they were.
- **Rotation and size handles.** Grab the handle on a selected object to turn it, and the ones on
  its sides to scale it, instead of typing numbers into the panel.
- **Characters that come in one piece.** In Final Bite and Forever the animals built from a
  skeleton — the ant, the bedbug, the bear — are now put together the way the game puts them
  together, so legs and necks join the body they belong to instead of splaying out from it. The
  same assembly was already done for Tasty Planet 2's skeletal characters.
- **Every Forever level shows its objects.** Four Mars levels (`mars1_4`, `mars1_5`, `mars1_9`,
  `mars1_10`) opened with their decorations and walls but no entities at all — the editor could not
  work out where one object ended and the next began inside those levels, so it drew nothing and
  gave no warning. Reading that data is fixed for the prefab names Forever actually uses (`h2o`,
  `bedrock`) and for walls whose shape is saved as a lower-case `circle`, so all 328 of the game's
  levels now load object-for-object.
- **A copy is kept before every save into a game folder**, so a change you decide against can still
  be undone from disk and not just with `Ctrl+Z`.
- **An installer** (`TastyEdit-Setup-1.0.0.exe`) that puts the program in your Start Menu with one
  shortcut per game, and offers to make level files open here when you double-click them.
- **The window says which game you're in**: the title bar and taskbar icon take the colour and mark
  of the level you have open, and the Start-Menu shortcuts use the same five tiles.
- A new look: light and dark themes, a toolbar with real icons, a start screen, and wording in the
  inspector that says what a setting does instead of only its stored name.

### Known limitations

- **No level from scratch yet** — you edit copies of levels that already exist.
- Four rarely-used numbers inside a Tasty Planet 2 decoration are carried through every edit
  untouched but can't be changed yet.
- A few Tasty Planet 2 characters put an outlying part (a horn, a foot) slightly off its body.
  Bodies, heads, tails and segmented necks line up; it is purely what you see in the editor, and
  saving is unaffected.
- Outline objects (walls, areas, water) and markers are drawn in file order, so which one is "on
  top" can differ from the game in a few spots. Purely a display detail; saving is unaffected.
- The published executables are unsigned, so Windows SmartScreen interrupts the first run
  (More info → Run anyway).

### How it's checked

Loading and saving is checked by the editor's own headless test runs, against the levels that are
installed on the machine — every one of the five games it can find. A level you open and save
without touching comes out exactly as it went in, and an edit only changes the object you changed.
Installing and reverting a mod is checked the same way against a throwaway sandbox, never a real
install. See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

---

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
