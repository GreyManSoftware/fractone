---
name: fractone-search
description: Search a user's FM9 presets for amps, cabs, drives or other blocks and the scenes using them, with the FracTone tools. Use for "which presets use the IIC++" or similar questions.
---

# FM9 preset search

Searches run on an index of every preset: its blocks, what each channel plays and which scenes engage it.

## Build the index

- **From the FM9:** `index_build` with sources `["fm9"]`. It reads all 512 presets in about 2-3 minutes and keeps
  the files as a backup.
- **From files:** pass `.syx` files or folders, either single presets or FM9-Edit / Fractal-Bot bank backups.
- Stores made with `store_preset` update the index. After edits made on the FM9 itself, rebuild it.
- `skipped` lists presets the index could not read, such as very old formats or empty placeholders.

## Search

`search` takes any mix of:

- `text`: words matched against preset, block and model names, e.g. `"IIC++"` or `"clean 2x12"`.
- `block`: e.g. `"Cab"` or `"Amp 2"`.
- `model`: part of a model or cab name.
- `cabBank` and `cabType`: a legacy cab by bank and number. DynaCab cabs are found by name instead.
- `includeUnplayed`: by default only channels some scene engages are listed. Set it to also find channels holding a
  model no scene plays.

Each hit gives the slot, preset, block, channel, model and the scenes that play it. Answer in those terms, e.g.
"Preset 12 'Justice' plays the IIC++ on Amp 1 channel B in scenes 3 and 6".
