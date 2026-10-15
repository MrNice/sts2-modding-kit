---
name: sts2-localize-zh
description: Add Simplified Chinese (or another language) to a Slay the Spire 2 mod's in-game text, change notes and store pages, using the game's own translations as the glossary so terms match what players already see. Use when a mod shows text to players, or when writing a mod's Workshop or Nexus page for Chinese players.
license: MIT
compatibility: Slay the Spire 2 mods. Uses the sts2-inspect tool (sts2-inspect-game skill) to read the game's localization tables.
metadata:
  version: "0.0.1"
---

# Localize a Slay the Spire 2 mod (Simplified Chinese first)

Simplified Chinese players are a large share of Slay the Spire's audience, so English plus Simplified Chinese
reaches many players for little work; other languages can fall back to English.

## 1. Use the game's words

The game is already translated. Your mod's "Smith" must be the word Chinese players see on the rest site button,
not a fresh translation. Extract the game's tables and look terms up by key:

```
sts2-inspect pck extract <SlayTheSpire2.pck> 'localization/(eng|zhs)/' loc/
```

Each table is `localization/<lang>/<table>.json`, a flat `{ "KEY": "text" }` map; the same key in `eng/` and
`zhs/` is the same string. To find a term, grep the English tables for the text, take its key, read the Chinese
value. Terms verified this way are in `references/glossary.md`.

Folder names are the game's language codes (`eng`, `zhs`, ...); the running game reports the player's choice in
`LocManager.Instance.Language`.

## 2. In-game text

Two options:

- **Reuse game strings** when one fits: `new LocString("<table>", "<KEY>").GetRawText()` is already translated
  into every language the game ships. Example: the Victory banner is `game_over_screen` / `BANNER.trueWin`.
- **Your own strings**: keep them in one class with one table per language, chosen by
  `LocManager.Instance.Language`, falling back to English:
  ```csharp
  public static Lines For(string? language) =>
      string.Equals(language, "zhs", StringComparison.OrdinalIgnoreCase) ? SimplifiedChinese : English;
  ```
  Keep the two tables the same shape and unit-test that (same count, no empty strings), so a missing
  translation fails a test instead of showing a blank line.

The game's own fonts render Chinese (banners, rich text labels, and labels you style with the game's theme
fonts such as `res://themes/kreon_bold_glyph_space_two.tres`). A plain Godot `Label` with no font set may use a
default font without Chinese glyphs, so style your labels with a game font.

A shared library such as BaseLib can merge mod localization files into the game's tables; for a handful of
strings, the in-code table above is simpler and has no dependency.

## 3. Writing the Chinese

- Full-width punctuation: `，。：；！？（）“”`, ellipsis `……`, em dash `——`. No spaces between Chinese characters;
  a space either side of embedded Latin text or numbers is common (`每 3 层`).
- Keep proper names and UI labels that stay English in the game as English (mode names on English-only buttons,
  your mod's name), and explain them once: `Normal（原版玩法）`.
- Match the tone of the original. Jokes and taunts need rewriting, not literal translation; have a native
  speaker check anything public if you can.
- If you used machine translation for a public post, say so in one line; Chinese communities are generally
  forgiving of honest machine translation and less of pretending.

## 4. Store pages and change notes

- **Steam Workshop**: each language has its own title and description (set the language on the item's edit page).
  A bilingual title such as `My Mod: English tagline | 中文标语` works for everyone without relying on that.
  Write change notes in both languages, English first, then the same notes in Chinese, in one note; Steam has
  one change note per update.
- **Nexus Mods**: one description; add a `简体中文：` paragraph. Mod names accept only English letters, numbers,
  spaces and `_ ' ( ) . -`, so the Chinese tagline goes in the summary or description instead. Each changelog
  line becomes a bullet, so put the Chinese lines after the English ones.
- **Images**: replace English text baked into your preview image with Chinese for Chinese posts (keep the logo).

## 5. Posting in Chinese communities

Big Slay the Spire communities are on Baidu Tieba, Bilibili, NGA and Xiaoheihe. Baidu rejects overseas phone
numbers at sign-up; Bilibili requires a Chinese-language quiz before commenting. If you can't get an account,
ask a Chinese-speaking player to share the post, credited as shared on your behalf (代发). Check each
community's rules first; some ban AI-assisted or AI-made mods.
