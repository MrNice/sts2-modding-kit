---
name: sts2-mod-release
description: Release a Slay the Spire 2 mod version - bump the version, write change notes (optionally English plus Simplified Chinese), build, package the manual-install zip, publish to the Steam Workshop with Mega Crit's ModUploader and to Nexus Mods, and verify each page. Use when shipping a new version of a StS2 mod.
license: MIT
compatibility: Windows, Steam running and signed in, Mega Crit's ModUploader, a Nexus Mods account with the mod already created. The person publishing confirms each public step.
metadata:
  version: "0.0.1"
---

# Release a Slay the Spire 2 mod

Publishing is public and partly irreversible. Ask the mod's author before each public step (push, Workshop
upload, Nexus upload), and never create accounts or enter passwords for them.

## 0. Decide the version

- **Patch** (0.4.0 → 0.4.1): fixes and compatibility. **Minor** (0.4 → 0.5): new features.
- If the mod talks between players, note whether its **network protocol** changed. If it did, everyone in a lobby
  must update and the notes must say so; if not, say old and new versions can play together.

## 1. Bump and write the notes

1. Set `"version"` in the mod manifest (`<Mod>.json`) — the copy you build from and the copy in the Workshop
   content folder.
2. Write `changelog-<version>.txt`: short player-facing lines, most important first. Describe what players see,
   not code ("Smith now goes last at rest sites", not "added SmithHold patch"). Only claim what you verified.
3. Bilingual notes: the English block, a blank line, then the same lines in Chinese under `<version>（简体中文）`.
   One note holds both so neither language waits on the other (see sts2-localize-zh for wording).

## 2. Build and package

1. Build in Release against the game version(s) you support. If you claim main *and* beta support, run the
   sts2-version-audit skill first.
2. Make the manual-install zip with this layout (players unzip into `<game>/mods/`):
   ```
   MyMod-<version>.zip
     MyMod/
       MyMod.dll
       MyMod.json
       INSTALL.txt   ← one paragraph: where to unzip, enable in the Mods menu, everyone in the lobby needs it,
                       which game versions it works on, where the Workshop page and logs are
   ```
3. Check the zip: the manifest inside shows the new version, and the dll is the one you just built (compare file
   time or hash with the build output).

## 3. Commit and push

Commit the version bump, notes and any source changes together; tag if you tag releases. Push.

## 4. Steam Workshop (ModUploader)

Mega Crit's ModUploader (https://github.com/megacrit/sts2-mod-uploader/releases) uploads a folder:

```
workshop/
  workshop.json   ← title, description (Steam BBCode), visibility, changeNote, tags, dependencies
  content/        ← exactly what gets installed: MyMod.dll, MyMod.json
  image.png       ← preview image
  mod_id.txt      ← written after the first upload; keeps later uploads on the same item
```

Run `ModUploader.exe upload -w <path to workshop>` with Steam running. Before running it:

- Put the change notes into `changeNote`.
- **The uploader sets the title and description from `workshop.json` every time.** Edits made on the Steam page
  (English) are overwritten by the next upload, so make them in `workshop.json`. Other languages' titles and
  descriptions are set per language on the Steam page and aren't touched.
- Copy the new build into `content/`.

Its log (`mod-uploader.log` next to the exe) ends with "Successfully uploaded ... with id <id>". Then open the
item's Change Notes page and check the new note is there in full.

## 5. Nexus Mods

On the mod's edit page (new upload form, 2026):

1. **Files** → upload the zip with **Add file**, then choose **Update existing file** and pick the current main
   file. (The per-file **Update** button does the same, starting from a file picker.)
2. Tick **Archive existing file** so players get the new one by default.
3. Set the display name (`My Mod <version>`) and file version; tick **Update mod version to match**.
4. **Add changelog**: one line per entry; each line becomes a bullet. English lines first, then Chinese.
5. **Save file**, then check the public page: version number in the header, and the Logs tab shows the entry.

Nexus mod names accept only English letters, numbers, spaces and `_ ' ( ) . -`. The description editor is
BBCode with a rich-text view; `[h1]`/`[h2]` from Steam don't exist there, use `[size=5][b]...[/b][/size]`.
Paste BBCode through the editor's **View source** toggle. If Save stays greyed out after a paste, type and delete
a character in the editor so the page notices the change.

## 6. After release

- Verify all three: your repository has the commit, Steam has the note, Nexus shows the version and changelog.
- Tell testers which version to expect, and keep the previous zip until the new one is confirmed working.
- Write down anything you assumed instead of tested; it's the first thing to check if reports come in.
