---
name: sts2-inspect-game
description: Look inside Slay the Spire 2 to plan or debug a mod - list a type's members, read a method's IL, check a hook target exists, pull scenes and localization out of the game pack. Use before writing a Harmony patch, when a patch doesn't fire, or when you need a node path, a private field name or the game's own text.
license: MIT
compatibility: Needs the .NET 9 SDK (or later) and a local Slay the Spire 2 install. Read-only; never modifies the game. Commands are shown for PowerShell or bash.
metadata:
  version: "0.0.1"
  game-versions: "v0.107.1 (main), v0.111.0 (beta) as of October 2026"
---

# Inspect Slay the Spire 2

Slay the Spire 2 mods are C# (.NET 9) assemblies loaded by the game, patching it with Harmony on top of Godot 4.5
(C#). The game's own code ships as a normal, unobfuscated .NET assembly, and its scenes and text ship in a Godot
pack. So you never have to guess: read the real code first, then write the patch.

## Where things are

| What | Where (relative to the game's install folder) |
|---|---|
| Game code | `data_sts2_windows_x86_64/sts2.dll` |
| Harmony, GodotSharp | `data_sts2_windows_x86_64/0Harmony.dll`, `GodotSharp.dll` |
| Scenes (.tscn), localization (.json), images | `SlayTheSpire2.pck` (Godot 4 pack) |
| Game version | `release_info.json` (`"version"`, `"branch"`) |
| Mods | `mods/<ModName>/<ModName>.dll` + `<ModName>.json` manifest |
| Player data, logs, configs | `%APPDATA%/SlayTheSpire2/` |

Namespaces start with `MegaCrit.Sts2.Core` (game logic) and `MegaCrit.Sts2.Core.Nodes` (Godot nodes, names start
with `N`, e.g. `NCombatRoom`, `NConfirmButton`).

## The tool

`scripts/Sts2Inspect` is a small .NET CLI with no package dependencies. Build it once, from this skill's folder:

```
dotnet build scripts/Sts2Inspect -c Release -o tools-out
```

In these skills, **`sts2-inspect` means `dotnet <path>/tools-out/sts2-inspect.dll`** (or the `sts2-inspect`
executable next to it); make an alias if you like. Run it with no arguments for the full usage. Examples quote
regexes with single quotes, which works in PowerShell and bash; in cmd.exe use double quotes. The commands
you'll use most:

| Question | Command |
|---|---|
| What does this class have? | `types <sts2.dll> =CombatManager` (`=` exact name; without it, substring) |
| What does this method actually do? | `il <sts2.dll> =CombatManager LoseCombat` |
| Does my hook target exist, and is it overloaded? | `has <sts2.dll> CombatManager.EndCombatInternal NConfirmButton._posOffset` |
| Which game members does my built mod use? | `refs <MyMod.dll>` |
| Which scenes/text files exist? | `pck list <SlayTheSpire2.pck> 'scenes/ui/'` |
| Get a scene or a language file | `pck extract <SlayTheSpire2.pck> 'localization/(eng|zhs)/' out/` |

Async methods compile to a state machine: the code of `Foo()` is in `Type+<Foo>d__N::MoveNext`, not in the
tiny stub `Type::Foo`. `il` with a method name prints both.

## How to find a hook point

1. **Start from what the player sees.** Extract the scene (`pck extract ... 'scenes/.*merchant'`) and read the node
   tree: node names, `unique_name_in_owner` (`%Name` lookups), offsets, `pivot_offset`. Then find the node's script
   class (`sts2-inspect types <dll> NMerchantInventory`).
2. **Follow the call chain in IL.** `il` resolves every call to `Type::Member`, so you can walk from a button's
   handler to the game logic. To find callers, dump everything once, `sts2-inspect il <dll> '' > all.il`
   (about 1.5M lines), then search it: `grep -n 'CombatManager::LoseCombat' all.il` (PowerShell:
   `Select-String 'CombatManager::LoseCombat' all.il`). Search for the member name alone: calls appear as both
   `call` and `callvirt`.
3. **Prefer events and public methods** (`CombatManager.TurnEnded`, `RunManager.RunStarted`) over patching. Patch
   when you must change behaviour, and patch the narrowest method that does it.
4. **Check the target with `has`** before writing `[HarmonyPatch]`. If it reports overloads, give argument types
   or use `TargetMethods()`.

## Patching rules that save hours

Read `references/patching-pitfalls.md` before your first patch. The short version:

- **One bad target can stop the whole mod.** `Harmony.PatchAll()` throws on the first ambiguous or missing
  target, and nothing after it gets patched. Patch each class separately (see the reference), and patch by name
  only when `has` shows exactly one method; otherwise pass argument types or use `TargetMethods()`.
- **Tiny methods can be inlined** by the JIT into callers, so a patch on a one-line getter may never run. Patch
  the caller, or change state the getter reads.
- **Prefixes on async methods** run when the call starts, not when it finishes. To skip an async method, set
  `__result = Task.CompletedTask` and return false.
- **Name-based reflection (`AccessTools.Field(..., "_foo")`) breaks silently** when the game changes. Keep the
  names in one place and check them with `has` on every game update (see the sts2-version-audit skill).
- **Init-only properties** (`init` setters, e.g. on records) can't be assigned from C#; use
  `Traverse.Create(obj).Property("Name").SetValue(v)`.

## Reading the game's text

Localization lives in `localization/<lang>/<table>.json` inside the pack (`eng`, `zhs`, ...; the folder names
are the game's language codes, also returned by `LocManager.Instance.Language`). Reuse the game's own strings
where you can, e.g. `new LocString("game_over_screen", "BANNER.trueWin").GetRawText()` gives "Victory" in every
language the game supports. To find a string: extract the tables and grep them.

## What not to do

- Don't redistribute extracted game files (scenes, images, text). Read them locally to learn names and layout.
- Don't decompile and paste large chunks of game code into your mod. Patch and call it instead.
