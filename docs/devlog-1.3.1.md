# SpaceScan 1.3.1: move a game to another drive with Shelf

20.10.2026. Item 2 of the ecosystem integration plan (Plan-integrari-ecosistem-2026-10-20).

## What

- The row menu item "Move with Shelf" is now **"Move this game to another drive (Shelf)"**: it says what it does.
- It shows only for a single folder that is a Shelf game Shelf itself marks movable (`canMove: true` in
  `data\shelf.games.json`, which Shelf sets for manually added and folder-scanned games; store games are their store's
  to move), and only while Shelf's card exists and its exe is there. The menu and the click both go through one new
  function, `Ecosystem.MoveCommand(node)`, which returns `{ exe, "--move \"<id>\"" }` or null, so what is shown and
  what runs can't disagree.
- After the hand-off SpaceScan calls `Ecosystem.Forget()`, so the next lookup reads Shelf's games file again and the
  folder's "· in Shelf" label follows the game once Shelf has written its new place (before, up to 2 minutes later).
- Works with line: "moves a game to another drive from the row menu". README: a "Works with Shelf and Backup Labs" line.

## Why

The hand-off had never been run end to end (vault: "netestat cap-coadă"), and everything it acts on comes from files
another program wrote. What it read was trusted more than it should be:

| Input | 1.3.0 | 1.3.1 |
|---|---|---|
| Card `exe` on `\\host\share\…`, `\\?\…`, a mapped network drive | `File.Exists` on it, which reaches that host | refused from the string and drive table alone, before any disk access |
| Card `exe` that is a `.bat` / relative path | started if it existed | refused: only an `.exe` on a local drive |
| Game `id` `--launch`, or with a quote | quote refused, `--launch` handed to Shelf | only `^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$` (Shelf's ids are UUIDs) |
| `installDir` that is UNC / `\\?\` / relative / not a string | UNC and relative kept (never matched a scanned folder, harmless) | skipped |
| `name` of any length | kept whole in the row label | cut at 200 characters; empty → the id |
| A shared file over 16 MB | read whole | not read (Shelf's games file is about 1 KB a game) |

BOM, `schema` other than 1, invalid JSON and wrong types were already read as "not there"; now covered by tests.
`Ecosystem.Local()` and the `.exe` rule sit in `Exe()`, so Backup Labs' card and the bought-something check get them too.

## How it was tested

**check.ps1** (reader level, in-process, Ecosystem.Folder redirected into %TEMP%): all sections OK, two new ones.

- MOVE: a `shelf.games.json` written **with a BOM** holding one movable game (UUID id) and 6 bad entries
  (`canMove:false`, `canMove:"true"`, id with quotes, id `--launch`, numeric id, `installDir` as an array), 3 odd folders
  (`\\host\share\Game`, `\\?\…\Dev`, `Games\Rel`), a 5000-character name, and junk array items (`"just a string"`, 7,
  null). Result: `SpaceScan.exe --move "0f8fad5b-d9cb-469f-a165-70867728950e"` for the good one; 0 of 6 bad entries
  offered, 0 of 3 odd folders named; the long name kept as 200 characters.
- MOVE INPUT: `schema: 2` → refused; the file padded past 16 MB → refused; Shelf's card exe on `\\host\share`,
  `\\?\…`, a `.bat`, a relative `SpaceScan.exe`, a missing exe → all refused; the good card back → offered again.

**End to end, isolated** (scratch `integ\spacescan\`, scripts kept there: `prep.js`, `spacescan-side.ps1`,
`run.ps1`, `drive.mjs`, `stop.ps1`):

- A test Shelf: the rig's built app `out-trainerfix` (Shelf 3.64.3 + trainer fix) under electron.exe, with APPDATA and
  LOCALAPPDATA in the scratch folder (its own profile and single-instance lock), window at -4000,-4000, CDP on 9335,
  the rig's launch blocker on, and the move's folder picker and message boxes replaced by a logger that answers Cancel,
  so no native dialog could reach the screen and nothing could be moved.
- Library: two synthetic games in `integ\spacescan\games`: "Tiny Game" (manual, 65,554 bytes) and "Store Game" (steam).
  A dev Shelf writes no card and no `shelf.games.json` (`ecosystem.ts`: `writes = app.isPackaged`), so `prep.js` wrote
  both into the scratch ProtagonistLabs folder in exactly the shape `ecosystem.ts` writes, card exe = electron.exe.
- SpaceScan side: **SpaceScan's ecosystem root can't be redirected for a window run** — `Ecosystem.Folder` comes from
  `Environment.SpecialFolder.ApplicationData`, which ignores the APPDATA variable — so SpaceScan's UI was not started.
  Instead the built SpaceScan.exe was loaded in PowerShell, `Ecosystem.Folder` set to the scratch folder, the games
  folder scanned with SpaceScan's own Scanner, and `MoveCommand` (what the menu shows and runs) asked for each folder:
  - Tiny Game: labelled, menu item shown, `exe=electron.exe args=--move "5e0c3f6a-9a51-4d2b-8f0e-1a2b3c4d5e6f"`
  - Store Game: labelled, `canMove=False`, menu item **not** shown
- Hand-off: that exact argument string, as a second launch in the same environment (a dev Shelf needs its app folder
  in front: `electron.exe app --move "…"`; an installed Shelf.exe takes SpaceScan's arguments as they are). Results:
  - second launch exited 0 (handed over to the running test Shelf);
  - Shelf asked for its move picker **480 ms** later: `Move "Tiny Game" into which folder?`, `openDirectory`; Cancel;
  - no error box in the window afterwards (CDP), and the game's folder still in place;
  - the store game's id handed over directly (SpaceScan never offers it): Shelf showed its own refusal ("came from a
    store, and only manually added and scanned games can be moved"), no picker, folder in place;
  - 0 blocked launches; the test Shelf stopped afterwards; the user's running Shelf (same 3 PIDs before and after) and
    `%APPDATA%\ProtagonistLabs` untouched; no SpaceScan was running and none was started with a window.

One thing found on the way, Shelf's side, not a SpaceScan problem: Shelf treats every absolute path in a second
launch's argv as "Add to Shelf", and that scan's report replaces whatever message the move set in the window. With the
dev app folder passed as an absolute path the store game's refusal vanished; passed relative, it shows. An installed
Shelf.exe gets no path from SpaceScan, so this doesn't happen in use.

## Untested

- The menu item in SpaceScan's own window (visibility, click, `Forget()` re-reading the label), for the reason above:
  the same `MoveCommand` it calls was tested, the WPF wiring around it was not.
- An installed, packaged Shelf.exe receiving the command (the dev build under electron.exe was used).
- A real move: the picker was always answered Cancel.
- A mapped network drive letter as the card's exe (the rule is `DriveInfo.DriveType != Network`; no such drive here).
