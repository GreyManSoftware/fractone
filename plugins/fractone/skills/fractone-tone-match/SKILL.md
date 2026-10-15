---
name: fractone-tone-match
description: Match an FM9 preset's tone to a target recording by fitting a Parametric EQ, using the FracTone tools (record or re-amp a take, fit, add a scene, audition). Use when the user wants a preset to sound more like a specific recording.
---

# FM9 tone matching

The match compares a **take**, the user playing the part through the preset and recorded from the FM9, with a
**target**, the recording to sound like. It fits the FM9's Parametric EQ to close the difference, writes the bands into
a spare EQ channel, and can add a scene that plays it, so the original scenes stay as they were.

## Work only on the scene you were asked about

Match, re-amp and level only the scene the user named. Don't record other scenes as references, balance against them
or change their settings unless the user asks. Other scenes can share what you change: a block channel, or the
Output block's master Level, which moves every scene. Before writing to a shared channel, check which scenes play it
(describe_preset lists them). If another scene plays it, write to a channel only the named scene plays, or ask.

## What you need

- A preset (`.syx`) with a `Parametric EQ 1` block. If it has none, add one into a shunt cell with `add_block`.
- **The target:** any common audio file (WAV, AIFF, FLAC, MP3, M4A, OGG or Opus) of the same part, ideally an isolated
  guitar track. Lossy files are fine: the fit trusts little above 4.5 kHz, where codecs cut first. Ask the user for a file, or capture one
  with `capture_playback` (below).
- **The take:** the same part played through the preset in one scene. Audition the preset with `audition`, ask
  the user to select that scene and get ready, then call `record` with source `"fm9"`. It waits for sound before
  it starts. 20-60 seconds of the part works well.
- **Or re-amp a DI** instead of a live take, when the user has a DI recording of the part: see below.

## Steps

1. Note which scene the take was played in.
2. Call `tone_match` with the preset, that scene, the take and the target. Ask for `newScene` (a free scene) so
   the fit gets its own scene, and `preview: true` for a WAV of the matched take.
   - The tool picks a Parametric EQ channel no scene plays, unless you name one.
   - Use `wetStart`, `targetStart` and `length` when the files don't start at the same point in the part.
3. Report the deviation before and after, and the fitted bands.
4. Offer the preview for listening, then `audition` of the new file so the user can compare the scenes.

## Capturing a target from a web page

`capture_media` records a page's own video or audio between two times, in a headless browser: nothing plays aloud,
and nobody has to press play. It takes real time.

- Only capture media the user has the right to use; they are responsible for the site's terms.
- It stops, with a screenshot, at bot checks, CAPTCHAs, sign-in or age walls and DRM. Don't try to get round any of
  them: show the user the screenshot and offer `capture_playback` while they play it in their own browser.
- It waits ads out (never records them) and declines consent dialogs with the most private option.
- First use downloads a headless browser (about 100 MB) into the `browserFolder` setting: say so first.

## Capturing a target from playback

`capture_playback` records whatever the PC plays, so a video or track can become the target.

1. Find the part's start and end times. Isolated-guitar uploads make the best targets.
2. Open the video in the browser at the start (for example `&t=95` on a YouTube link), paused.
3. Call `capture_playback` with the part's length, and press play in the same turn (or ask the user to). It waits
   for sound, so the order doesn't matter.
4. Check the reply. A "full mix" hint means bass, kick or cymbals are in it; the match will chase them. Prefer an
   isolated track, or separate the guitar first (below).

Everything the PC plays is recorded: skip or wait out ads, and ask the user to silence notifications.

## Separating the guitar from a full mix

Matching runs on the CPU and needs no GPU. Separation is the one heavy step, and it's only needed when the target is
a full mix (the "full mix" hint). Skip it for isolated stems and isolated-guitar videos.

1. Pick the part: 45-60 s is plenty. `separate` works on that window only (`start`, `length`), never the whole song.
2. Call `separate` with `plan: true`. Tell the user the device (NVIDIA GPU, Apple silicon GPU or CPU) and the
   estimated time. On a CPU, say plainly that it's slower but works. On first use, also tell them what it installs
   (Python, PyTorch for their hardware and Demucs; the size and folder are in the plan) and get a yes.
3. Run it. Use the stem as the target, and pass its bleed notes on: bass or kick bleed makes the lows less
   trustworthy, cymbal bleed pushes the match brighter.

## Re-amping a DI

`reamp` plays a DI through the preset and records each scene in one sample-locked ASIO stream, so no one has to play.

- The preset must have **Input 4** (the USB input) in each scene's path. The tool refuses a preset without it.
- It plays through the user's speakers. Say so, and get a yes, before calling it.
- The FM9's ASIO driver must be free: ask the user to close Reaper or any other ASIO program first.
- With `stem` it compares each take with the target; with `match: true` it also fits a Parametric EQ channel per
  scene into `matched.syx`. With `level: true` it also levels the preset (see the fractone-levelling skill).
- Silence at the start of the DI is trimmed up to the first note unless `trim` is given. A silent take means the DI
  didn't reach that scene: check Input 4's cabling and the Output level.

## Choosing the cab: shortlist, then the player's ears

When a match stays far off, or the user relies on an IR blend, look for a factory cab (or a DynaCab placement) that gets
close with only a gentle EQ after it. One cab and broad EQ moves of 3 dB or less can beat a downloaded IR (Ride the
Lightning, 2026-10-14: 3.35 dB with the user's IR blend, 2.75 dB with a factory 4x12 and a gentle PEQ).

**The gap is a shortlist, not a verdict.** It rewards the low-mid body a stem carries from bleed and mastering. On Ride
the player hated the closest cab ("too much fat", lost the sustained, scooped sound) and found the cab from his own IR
blend, alone, far closer by ear. Use `rank_cabs` to narrow the field; the player chooses.

1. Re-amp the scene once with its **Cab block and any Parametric EQ you will fit bypassed** (`set_block`, then `reamp`).
   Keep the amp, drive and everything before the cab as they are. If the player plays rather than uses a DI, record a
   take instead.
2. Call `rank_cabs` with that take and the stem. It predicts every cab from its measured response, so nothing else
   plays. `include` picks the pool: `"factory"` (default), `"dynacab"` (DynaCab types and mics at a few positions, 1 cm
   out) or `"both"`.
3. **Prefer the cab family of the player's own IR or rig** over a slightly smaller gap: pass a `filter` with its name
   (`"1960"`, `"V30"`, `"Recto"`) or size (`"4x12"`). Many cabs land within a few tenths of a dB, and a 1x8 practice
   amp at the top of a metal song's list is a near tie to ignore, not a discovery.
4. Apply **one change at a time** as an A/B, never over the user's sound: `copy_channel` the Cab channel the scene
   plays onto a channel no scene plays, `set_channel` the returned `cabSettings` there (they set Mode, the cab or the
   DynaCab type, mic, position and Distance in cm, and mute slot 2) plus `Level: "0"`, `Low Cut: "20"`,
   `High Cut: "20000"`, `Proximity: "0"`; then `compose_scene` a free scene as a copy of the original playing that
   channel. Try the cab alone first; add the returned `parametricEqSettings` on a free Parametric EQ channel as a
   separate step only if the player wants it.
5. **Keep the player's post-cab EQ** (e.g. their Graphic EQ) engaged in every comparison scene: it is part of their
   tone, not something the fit replaces.
6. Level the scenes (`level: true`, `levelTarget: "match <original>"`) so loudness doesn't decide, report the gaps,
   then let the player listen and choose. Their ears outrank the score.

## Judging the result

- Compare a preset only with **its own song**. Never tune a preset against another song's recording or another preset.
- A match fixes EQ, not gear. If the result is still far off, the amp, drive or cab choice is the likelier cause; see
  the fractone-tone-designer skill.
