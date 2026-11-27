# TastyEdit

**A level editor for the Tasty Planet games.** Open a level, click on anything, change where it is
and what it looks like, save it — then play your level in the game.

One program covers five games: **Tasty Planet**, **Tasty Planet 2: Back for Seconds**, **Tasty
Blue**, **Tasty Planet Forever** and **Tasty Planet Final Bite**. You just pick a level file;
TastyEdit works out which game it came from.

You can also make a **mod**: your levels packed into one file that somebody else installs into
their game, and takes back out again, with a click.

What's in this release is listed in [CHANGELOG.md](CHANGELOG.md).

---

## Get started

**The easy way is the installer.** Download `TastyEdit-Setup-1.0.1.exe`, run it, and it puts the
program in your Start Menu with a shortcut for each of the five games. It installs for your Windows
user account only — into `%LOCALAPPDATA%\Programs\TastyEdit` — and doesn't ask for administrator
rights.

**Or take the zip** if you'd rather just have a folder: download `TastyEdit-1.0.1-win-x64.zip`,
unzip it anywhere and open `TastyEdit.exe`. Nothing gets installed and nothing else gets touched.

Either way, Windows will probably interrupt the first run with a blue *"Windows protected your PC"*
window. That happens to any small home-made program whose author hasn't bought a code-signing
certificate. Click **More info**, then **Run anyway**.

**You need to own the games.** TastyEdit doesn't contain any levels, pictures or models from them —
it reads the artwork straight from your own game folders, which is why you see the real objects
instead of coloured boxes.

## Open a level

**File ▸ Open level…**, then go to your game and pick a level:

| Game | Where its levels are | The files |
|---|---|---|
| Tasty Planet | `<game>\assets\levels\` | `.xml` |
| Tasty Planet 2: Back for Seconds | `<game>\assets\levels\` | `.bin` |
| Tasty Blue | `<game>\assets\editabledirect\levels\` | `.bin` |
| Tasty Planet Forever | `<game>\assets\editabledirect\levels\` | `.bin` |
| Tasty Planet Final Bite | `<game>\assets\editabledirect\levels\` | `.bin` |

`<game>` is that game's own folder, usually something like
`…\Steam\steamapps\common\Tasty Planet Back for Seconds`. The original Tasty Planet is the one game
whose levels aren't `.bin` files — they're plain XML, and they open the same way.

The whole level appears on the left, and **View ▸ Fit** frames it nicely. On the right you can tick
the separate levels inside the file on and off, so you only see what you're working on; a big area
or a water region shows as an outline you can grab and reshape.

## Change something

Click any object in the level. The panel on the right fills in with what you can change, and each
box takes effect as soon as you set it:

- **Where it is** — X and Y, or just drag it.
- **Which way it faces, and how big it is** — grab the handles that appear on the selected object
  to turn it or stretch it, or set rotation, the flip left/right and up/down switches and the size
  in the panel.
- **What it looks like** — its picture, its size, its colour and how see-through it is.
- **How it behaves** — walls, areas, water and the moving things each bring their own settings:
  what they do to the player, what they create, how far they grow.
- **Which of the file's levels it belongs to.**

You can also copy, paste, duplicate and delete things, place brand-new objects, and lay a second
level faintly behind the one you're editing so you can line the two up. In Tasty Planet 1 there's
extra room to work: **View ▸ Background tilemap…** paints the tile layer, and the entity types come
from the game's own tables, which you can edit and add to.

## Mods: sharing what you make

A mod is one `.tpm` file — your levels and any extra artwork, plus a short note saying which game
it's for.

**To play one:** open TastyEdit and click **Manage mods…** on the start screen. Pick the game,
pick the `.tpm`, install it. The game's own files are copied aside before anything is written, so
**Revert** puts the game back exactly as it was, and you can keep several mods installed and switch
between them. Double-clicking a `.tpm` in Windows takes you straight here too.

**To make one:** **Mod ▸ Create mod…** names it, chooses the game and makes a folder for it
somewhere *outside* your game install. From then on, saving a level puts a copy in the mod folder
as well as wherever else you send it; **Mod ▸ Add asset to mod…** brings in an extra picture or
file. When it's finished, **Mod ▸ Publish mod as .tpm…** makes the one file you can send to
somebody else.

There's no mod loader in these games and TastyEdit doesn't install one: a mod is your files in
place of the game's, with the backups and the way back handled for you.

## Big levels stay quick

Some levels hold thousands of objects. TastyEdit only draws what's on screen, so a huge level is
still comfortable to move around — and even when you zoom way out, objects keep their real pictures
instead of turning into tiny crosses. Turn **View ▸ Always draw sprites** off if you'd rather have
the lighter, simpler view.

Animated characters are put together the way the game assembles them, so a Final Bite ant arrives
with its legs attached rather than as a pile of parts.

## Moving around

| To | Do this |
|---|---|
| Pick something | left-click it |
| Move it | drag it, or type new numbers on the right |
| Turn or resize it | drag the handles on the selection |
| Move the view | **hold the middle mouse button and drag** |
| Zoom in and out | scroll the mouse wheel |
| Copy, paste, duplicate, delete | right-click, or `Ctrl+C` `Ctrl+V` `Ctrl+D` `Del` |
| Undo a mistake | `Ctrl+Z` (`Ctrl+Y` redoes it). Every change is one step back. |
| See the whole level | `View ▸ Fit` |

## Saving

**File ▸ Save…** writes a copy wherever you choose. **Save (in place)** replaces the file inside your
game folder — and before it does, TastyEdit keeps a copy of the file it's about to replace, in a
backups folder next to the program, so a change you decide against can still be recovered.

Two things this editor always tries to keep true:

- if you open a level and save it **without changing anything**, the file comes out exactly as it
  went in;
- when you change one thing, only that thing is touched — the rest of the level is left alone.

If TastyEdit can't save part of a level faithfully, it won't pretend to: you'll hear about it instead
of getting a broken file.

## Good to know

- **Everything shows as little X marks?** Your level isn't inside its game folder, so TastyEdit
  can't find the game's artwork. Open the file from the game's own folder and the pictures come
  back. (Editing works either way.)
- **A setting you expected isn't there?** Open an issue and say which game, which object and which
  setting.
- **Nothing leaves your computer.** No internet, no accounts, no statistics. Your recent-files list
  and view settings are kept in `%LOCALAPPDATA%\TastyEdit`.
- **Double-clicking level files.** The installer offers to make levels open straight in TastyEdit;
  you can also turn it on or off later from **File ▸ Register .bin association** / **File ▸
  Remove .bin association**. It's your choice either way, it's reversible, and it doesn't touch
  your game. One extension can only have one icon in Windows, so the per-game tiles show up on the
  Start-Menu shortcuts and on the window itself instead.
- **The window's icon is the game you're in.** So is the taskbar button, which is what makes a
  screenshot of a bug report say which game it came from — the title bar carries the version too.

## Not in this version

- Making a level from nothing — you edit copies of levels that already exist.
- Four rarely-used numbers inside a Tasty Planet 2 decoration that decide where it gets cut off.
  They're carried through untouched, you just can't change them yet.
- Modding that adds a loader or patches a game's code. Only file replacement, with backups.

## Building it yourself

You don't need to — the installer or the zip above is the whole program. If you'd rather compile it
or change it, [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) covers the build, the checks the program
runs on itself, and how a release is made. [docs/MODFORMAT.md](docs/MODFORMAT.md) describes the
`.tpm` file, so you can write a mod by hand if you'd rather not use the editor.

## Credits

TastyEdit stands on earlier work by three people, and I want to be straight about whose is whose:

- **RemagOfficial** — my own first pass at reading Tasty Planet 2 level files, which everything here
  grew out of.
- **LazyRares** — the [Tasty Planet 2 level editor](https://github.com/lazyRares/Tasty-Planet-2-Level-Editor),
  the well-made one that showed what a proper editor for these games should look like.
- **Sharkie** — for adding partial Tasty Blue support, which is where the Tasty Blue side of this
  work started.

The file formats for all five games were then worked out by opening up the games themselves, and
every rule this editor writes is checked by a test.

TastyEdit is a fan tool for games you own. It ships no game content, and it isn't affiliated with or
endorsed by the games' rights holder.

## License

Released under the **GNU GPL v3** — see [LICENSE](LICENSE). You may use, study, change and share this
program, and anything you pass on has to carry the same freedoms.

Copyright © 2026 RemagOfficial

It comes with no warranty at all, which is the same advice as above: **keep copies of your levels**,
which the editor now does for you before it saves into a game folder.

## Found a problem?

Open an issue here and say which game, which level, what you did and what happened instead. The
title bar shows which version you're running and the window icon shows which game the level is, so a
screenshot usually answers half of it already.
