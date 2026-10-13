# Fractone

**Unofficial tools for Fractal Audio's FM9** (Axe-Fx III and FM3 planned). Not affiliated with Fractal Audio Systems.

Drive a Fractal Audio FM9 from an AI coding agent. An MCP server (`fractone-mcp`) and six skills let Claude Code, Pi or
any MCP client:

- read, describe, back up, edit, audition, store and restore presets;
- save, list and apply FM9-Edit block-library favourites;
- search every preset for a model or cab and the scenes that play it;
- turn knobs live, switch channels, bypass and scenes, as FM9-Edit does, with diff, undo and snapshots;
- design a preset from a description of a tone, with the same checks as the FracAI tone designer;
- match a preset's tone to a recording with a fitted Parametric EQ, the target captured from what the PC plays or from a
  web page's media if need be;
- separate the guitar from a full mix (an optional add-on, installed on first use; NVIDIA GPU, Apple silicon or CPU);
- re-amp a DI through every scene over the FM9's USB audio, and level scenes from those takes or from live playing.

**Unofficial.** Not made, endorsed or supported by Fractal Audio Systems. FM9 and FM9-Edit are their trademarks.
Writing presets to hardware carries risk; keep backups (see the safeguards below).

**Capturing media from web pages.** `capture_media` records a page's own video or audio in a headless browser.
Only capture media you have the right to use, and you are responsible for following each site's terms. It never gets
past bot checks, CAPTCHAs, sign-in walls or DRM, and never records ads: it waits ads out, declines consent dialogs with
the most private option (or stops), and otherwise stops and says why, with a screenshot.

## Requirements

- Windows, with the FM9 connected by USB. The server reaches it through Windows MIDI, so it runs on the PC itself,
  not in a container.
- The [.NET 10 SDK](https://dotnet.microsoft.com/download), which provides `dnx` to run the server.
- FM9 firmware 12. Writes are refused on other firmware; reading still works.
- FM9-Edit installed and opened once with the FM9 connected. The server reads FM9-Edit's own parameter dictionary
  from your machine (`%APPDATA%\Fractal Audio\FM9-Edit`) rather than shipping a copy.

## Install

**Claude Code**

```
/plugin marketplace add GreyManSoftware/fractone
/plugin install fractone@fractone
```

**Pi**

```
pi install npm:fractone
```

Pi has no permission prompts, so the package's extension asks you before any call that changes what the FM9 holds or
plays (storing, auditioning, selecting a preset).

**Any other MCP client:** run `dnx Fractone` as a stdio server.

## Set up (optional)

Everything works with defaults. To choose folders and safeguards:

```
dnx Fractone setup --careful --writable 500-505
```

| Safeguard | What it does | Default |
|---|---|---|
| `writableSlots` | Only these preset slots can be written | every slot |
| `backupBeforeStore` | Keep the slot's previous preset in `backups` | off |
| `verifyAfterStore` | Read the stored slot back and compare | off |
| `refuseLoadIfBufferChanged` | Refuse to load over unsaved edits on the FM9 | off |
| `confirmWrites` | A store returns a code; the agent must ask you and call again with it | off |

`--careful` switches every one on. The settings live in `%APPDATA%\fractone\settings.json`. Whatever the settings,
every store reports what it replaced, checks the FM9's copy before storing, and undoes a failed import.

## Agents in containers

Docker on Windows cannot reach USB MIDI, so the server always runs on the PC. An agent in a container can use it over
HTTP: start `dnx Fractone --http` on the PC and point the agent at `http://host.docker.internal:39009/mcp` with header
`Authorization: Bearer <token>`. The token is in `%APPDATA%\fractone\http-token`. Share the work folder with the
container so both sides see the same files.

<!-- mcp-name: io.github.greymansoftware/fractone -->
