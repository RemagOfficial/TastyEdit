# TastyEdit

A level editor for the **Tasty Planet** games. Open a level, move things around, change how they
look, save it back — and the game loads your level like it was always there.

It handles four games in one program: Tasty Planet 2: Back for Seconds, Tasty Blue, Tasty Planet
Forever and Tasty Planet Final Bite. You point it at a level file and it works out which game it
belongs to.

> **This is a beta.** It has been checked against every shipped level of all four games, but it is
> a brand new editor: keep backups of the levels you change, and tell me what breaks.

---

## Get going

1. Download **`TastyEdit-0.9.0-beta-win-x64.zip`** from the latest release on this page.
2. Unzip it into a folder of its own — anywhere is fine, it needs no installation and never writes
   outside itself except where you tell it to save.
3. Double-click **`TastyEdit.exe`**.

Windows will probably interrupt the first run with a blue *"Windows protected your PC"* box, because
this editor isn't bought from the Store or signed with a certificate. Choose **More info → Run
anyway**. That warning is normal for hobby tools; if you'd rather not take my word for it, the
source is right here and you can build it yourself.

**You need to own the games.** TastyEdit contains no levels, art or models — it borrows the
graphics from your own game folders to draw the level, which is why it shows real sprites instead of
boxes.

## Open a level

**File ▸ Open level…** and find your game's level files:

| Game | Where the levels live |
|---|---|
| Tasty Planet 2: Back for Seconds | `<game>\assets\levels\` |
| Tasty Blue | `<game>\assets\editabledirect\levels\` |
| Tasty Planet Forever | `<game>\assets\editabledirect\levels\` |
| Tasty Planet Final Bite | `<game>\assets\editabledirect\levels\` |

Pick any `.bin`. The whole level appears; **View ▸ Fit** frames it.

Once you've opened a level from a game folder, **File ▸ Open recent** remembers it, and
*File ▸ Register .bin association* makes double-clicking a level file in Windows open it straight
in TastyEdit. That registration is yours only, reversible from the same menu, and never touches
the game.

## Moving around

| To | Do |
|---|---|
| Select something | left-click it |
| Move it | drag it, or type new numbers in the panel on the right |
| Pan the view | **hold the middle mouse button and drag** |
| Zoom | mouse wheel |
| Copy / paste / duplicate / delete | right-click, or `Ctrl+C` `Ctrl+V` `Ctrl+D` `Del` |
| Go back | `Ctrl+Z` (and `Ctrl+Y` to come forward). Every change is one step. |
| Jump to the whole level | `View ▸ Fit` |

The right-hand panel is where the real editing happens: it shows the fields of whatever you have
selected, and each box applies as soon as you change it.

## What you can change

- **Position, rotation and facing** — including the flip left/right and up/down switches.
- **The picture it uses** — swap any decoration or entity onto another sprite from the game's own
  art, and change its colour and transparency.
- **Size, and which level of the file it belongs to.**
- **Walls, areas and water** — their shapes, and the behaviour settings the game reads from them.
- **Entities** — their type and the behaviour data behind them. TastyEdit only offers entity types
  your game actually knows about; an unknown one is silently dropped by the game when it loads the
  level, so the editor won't let you write one by accident.
- **Add and remove things** — copy, paste, duplicate, delete, and place brand-new entities.
- **Compare against another level** — load a second level faintly behind the one you're working on
  (`Multilevel / Goals…` and the reference view) so you can line things up.

Anything a level file holds that the editor can't faithfully write back is simply not offered,
rather than being offered and quietly breaking the file.

## Saving

**File ▸ Save…** writes a copy wherever you choose. **Save (in place)** overwrites the file in your
game folder — so before you use it, make a copy of the game's `levels` folder. If a level stops
loading, that copy is the fix.

Two promises the editor checks constantly while it runs:

- a level you opened and saved **without changing anything** comes out byte-for-byte identical to
  the file you started from;
- a change only touches the object you changed, never other parts of the file.

## Good to know

- **Biggest levels.** Zoomed way out on a huge level, everything can be drawn as tiny sprites
  instead of pictures. `View ▸ Always draw sprites` is on by default; turn it off if a very dense
  level starts feeling sluggish.
- **Everything looks like X marks?** Your level isn't inside its game folder, so the editor can't
  find the game's art. Open the file from the game's own folder and the sprites come back. (Editing
  still works either way.)
- **A field you expected isn't there?** Tell me which game, which object and which field — that's
  the main thing a beta is for.
- **Nothing leaves your PC.** No internet, no accounts, no statistics. Your recent-files list and
  view settings live in `%LOCALAPPDATA%\TastyEdit`.

## Not in this version yet

- Making a brand-new level from scratch — you edit copies of levels that already exist.
- Four rarely-used clip rectangle numbers inside a decoration. They are carried through every edit
  untouched, you just can't change them yet.
- Creating whole new games' formats — only the four above.

---

## Building it yourself (optional)

You don't need to — the zip above is the whole program. If you'd rather compile it, or change it,
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) covers the build, the self-checks the program runs, and
how a release is made.

## Credits

TastyEdit stands on earlier work by three people, and I want to be straight about whose is whose:

- **RemagOfficial** — the original [Tasty Planet 2 level-parsing Python
  scripts](https://github.com/RemagOfficial/Tasty-Planet-2-level-editor), which everything here grew
  out of. That is me, the author of TastyEdit.
- **LazyRares** — the [Tasty Planet 2 level editor](https://github.com/lazyRares/Tasty-Planet-2-Level-Editor),
  the well-made one that showed what a proper editor for these games should look like.
- **Sharkie** — for adding partial Tasty Blue support, which is where the Tasty Blue side of this
  work started.

The file formats for all four games were then worked out from the games themselves, and every rule
this editor writes has a check behind it.

TastyEdit is a fan tool for games you own. It ships no game content and is not affiliated with or
endorsed by the games' rights holder.

## License

Released under the **GNU GPL v3** — see [LICENSE](LICENSE). You may use, study, change and share
this program, and any version you distribute must carry the same freedoms.

Copyright © 2026 RemagOfficial

It comes with no warranty at all, which is also the honest advice in the Saving section above:
**back up your levels before you overwrite them.**

## Found a problem?

Open an issue here and include: which game, which level, what you did, and what happened instead.
The title bar shows the version you're running, so a screenshot usually answers half of it.
