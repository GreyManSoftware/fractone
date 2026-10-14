# FracTone

**Unofficial tools for Fractal Audio's FM9** (Axe-Fx III and FM3 planned). Not affiliated with Fractal Audio Systems.

An MCP server and agent skills for the Fractal Audio FM9. With them, an AI agent such as Claude Code or Pi can read,
back up, edit, audition, store and restore presets. It can also save and apply FM9-Edit favourites, search every
preset, design presets from a description of a tone, and match a preset to a recording.

**Unofficial.** Not made, endorsed or supported by Fractal Audio Systems. FM9, FM9-Edit and Fractal Audio are their
trademarks. Writing presets to hardware carries risk; switch on the safeguards and keep backups.

**Capturing media from web pages.** `capture_media` records a page's own video or audio in a headless browser.
Only capture media you have the right to use, and you are responsible for following each site's terms. It never gets
past bot checks, CAPTCHAs, sign-in walls or DRM, and never records ads: it waits ads out, declines consent dialogs with
the most private option (or stops), and otherwise stops and says why, with a screenshot.

- Install and use: [plugins/fractone/README.md](plugins/fractone/README.md)
- Tools and settings: `dnx FracTone --help`

## What is here

| Folder | What it is |
|---|---|
| `src/Fractone.Mcp` | The MCP server (`fractone-mcp`): tools over stdio, or local HTTP with a token |
| `src/Fractal.Fm9` | The FM9 itself: preset format, MIDI, the verified store, FM9-Edit favourites, preset index, recording, tone match |
| `src/Fractal.Application`, `src/Fractal.Core`, `src/Fractal.Ai` | The tone-design model, its repairs and checks, and the audio analysis behind tone matching |
| `plugins/fractone` | The skills, as a Claude Code plugin and a Pi package |
| `tests` | Unit and end-to-end tests |

## Data this project does not ship

- **FM9-Edit's parameter dictionary.** It is Fractal's data and also lists your own preset names. The server reads
  your copy, which FM9-Edit caches in `%APPDATA%\Fractal Audio\FM9-Edit` once it has connected to the FM9.
- **Amp levels from someone's presets.** Every model starts at a default level. You can point `FRACTONE_AMP_LEVELS` at a
  table built from your own presets.

## Building

```
dotnet build Fractone.slnx
dotnet test Fractone.slnx
```

Tests that need the dictionary run only where FM9-Edit is installed, and are skipped elsewhere with a note.
The media-capture tests need the headless browser: run `fractone-mcp setup-browser`, then set `FRACTONE_BROWSER_FOLDER`
to its folder.
Before a release, run them all on such a machine and do the hardware checklist in `docs/release-checklist.md`.
Builds don't send Avalonia's build telemetry (the probe GUI uses Avalonia): `Directory.Build.targets` switches it off, and
CI also sets `AVALONIA_TELEMETRY_OPTOUT=1`, the opt-out Avalonia documents.

## Licence

MIT, except third-party data listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
