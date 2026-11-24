# TastyEdit

**A level editor for the Tasty Planet games.** Open a level, click on anything, change where it is
and what it looks like, save it — then play your level in the game.

One program covers four games: **Tasty Planet 2: Back for Seconds**, **Tasty Blue**, **Tasty Planet
Forever** and **Tasty Planet Final Bite**. You just pick a level file; TastyEdit works out which
game it came from.

> **It's a beta.** Loading and saving has been checked against every level that shipped with the
> four games, but this is a brand new tool. Keep copies of the levels you change, and tell me what
> goes wrong.

What's in this release is listed in [CHANGELOG.md](CHANGELOG.md).

---

## Get started

1. Download **`TastyEdit-0.9.0-beta-win-x64.zip`** from the latest release on this page.
2. Unzip it into a folder — anywhere you like. There's nothing to install.
3. Open **`TastyEdit.exe`**.

Windows will likely interrupt the first run with a blue *"Windows protected your PC"* window. That
happens to any small home-made program that hasn't been bought and signed by its publisher. Click
**More info**, then **Run anyway**.

**You need to own the games.** TastyEdit doesn't contain any levels, pictures or models from them —
it reads the artwork straight from your own game folders, which is why you see the real objects
instead of coloured boxes.

## Open a level

**File ▸ Open level…**, then go to your game and pick any `.bin` file:

| Game | Where its levels are |
|---|---|
| Tasty Planet 2: Back for Seconds | `<game>\assets\levels\` |
| Tasty Blue | `<game>\assets\editabledirect\levels\` |
| Tasty Planet Forever | `<game>\assets\editabledirect\levels\` |
| Tasty Planet Final Bite | `<game>\assets\editabledirect\levels\` |

The whole level appears on the left, and **View ▸ Fit** frames it nicely. On the right you can tick
the separate levels inside the file on and off, so you only see what you're working on; a big area
or a water region shows as an outline you can grab and reshape.

## Change something

Click any object in the level. The panel on the right fills in with what you can change, and each
box takes effect as soon as you set it:

- **Where it is** — X and Y, or just drag it.
- **Which way it faces** — rotation, and the flip left/right and up/down switches.
- **What it looks like** — its picture, its size, its colour and how see-through it is.
- **How it behaves** — walls, areas, water and the moving things each bring their own settings:
  what they do to the player, what they create, how far they grow.
- **Which of the file's levels it belongs to.**

You can also copy, paste, duplicate and delete things, place brand-new objects, and lay a second
level faintly behind the one you're editing so you can line the two up.

## Big levels stay quick

Some levels hold thousands of objects. TastyEdit only draws what's on screen, so a huge level is
still comfortable to move around — and even when you zoom way out, objects keep their real pictures
instead of turning into tiny crosses. Turn **View ▸ Always draw sprites** off if you'd rather have
the lighter, simpler view.

## Moving around

| To | Do this |
|---|---|
| Pick something | left-click it |
| Move it | drag it, or type new numbers on the right |
| Move the view | **hold the middle mouse button and drag** |
| Zoom in and out | scroll the mouse wheel |
| Copy, paste, duplicate, delete | right-click, or `Ctrl+C` `Ctrl+V` `Ctrl+D` `Del` |
| Undo a mistake | `Ctrl+Z` (`Ctrl+Y` redoes it). Every change is one step back. |
| See the whole level | `View ▸ Fit` |

## Saving

**File ▸ Save…** writes a copy wherever you choose. **Save (in place)** replaces the file inside your
game folder — so before you do that, copy the game's `levels` folder somewhere safe. If a level ever
stops loading, put that copy back and you're fine.

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
- **A setting you expected isn't there?** Tell me which game, which object and which setting. That's
  exactly what a beta is for.
- **Nothing leaves your computer.** No internet, no accounts, no statistics. Your recent-files list
  and view settings are kept in `%LOCALAPPDATA%\TastyEdit`.
- **Double-clicking level files.** After **File ▸ Register .bin association**, Windows opens level
  files straight in TastyEdit. It's your choice, it's reversible from the same menu, and it doesn't
  touch your game.

## Not in this version

- Making a level from nothing — you edit copies of levels that already exist.
- Four rarely-used numbers inside a decoration that decide where it gets cut off. They're carried
  through untouched, you just can't change them yet.

## Building it yourself

You don't need to — the zip above is the whole program. If you'd rather compile it or change it,
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) covers the build, the checks the program runs on itself,
and how a release is made.

## Credits

TastyEdit stands on earlier work by three people, and I want to be straight about whose is whose:

- **RemagOfficial** — my own first pass at reading Tasty Planet 2 level files, which everything here
  grew out of.
- **LazyRares** — the [Tasty Planet 2 level editor](https://github.com/lazyRares/Tasty-Planet-2-Level-Editor),
  the well-made one that showed what a proper editor for these games should look like.
- **Sharkie** — for adding partial Tasty Blue support, which is where the Tasty Blue side of this
  work started.

The file formats for all four games were then worked out by opening up the games themselves, and
every rule this editor writes is checked by a test.

TastyEdit is a fan tool for games you own. It ships no game content, and it isn't affiliated with or
endorsed by the games' rights holder.

## License

Released under the **GNU GPL v3** — see [LICENSE](LICENSE). You may use, study, change and share this
program, and anything you pass on has to carry the same freedoms.

Copyright © 2026 RemagOfficial

It comes with no warranty at all, which is the same advice as above: **keep copies of your levels
before you overwrite them.**

## Found a problem?

Open an issue here and say which game, which level, what you did and what happened instead. The
title bar shows which version you're running, so a screenshot usually answers half of it already.
