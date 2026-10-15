---
name: fractone-levelling
description: Level an FM9 preset's scenes (equal, matched or leads louder) and its overall output level with the FracTone tools. Use when the user wants scenes or presets levelled or balanced in volume.
---

# Preset and scene levelling

The Output block comes after every other block, so a change there moves what the FM9 sends by exactly that many dB.
Levelling uses two of its settings:

- **Scene N Level** (-20 to +20 dB per scene) balances the scenes against each other, by loudness.
- **Level**, the master (-80 to +20 dB), sets the whole preset so its loudest scene peaks at a target. The default is
  -9 dBFS, about a quarter into the red of the FM9's output meters. Fractal calibrates the meters with 12 dB of
  headroom at the red line, which puts the red line at about -12 dBFS.

The master takes the part of the move every scene shares, so the trims stay near 0 dB and only carry the differences.
Short probes settle within 0.3 dB in two or three rounds.

## Work only on the scenes you were asked about

Level only the scenes the user named. Levelling some scenes uses their own trims; the master Level moves every scene,
so the tools change it only when all eight scenes are levelled together.

## With a DI (best: exact and hands-free)

If the user has a DI recording, call `reamp` with `level: true`, naming every scene to level.

- It levels first from a few short probes around the DI's highest peak, so it knows straight away when a scene is far
  too loud, then records the full takes already levelled. `levelOnly: true` stops after levelling.
- It plays through the speakers, so ask first unless the user has said they're off.
- The preset needs Input 4, the USB input.

## While the player plays

1. **Agree the balance.** Most players want every scene equal (`"match 1"`, the default), with leads a little louder
   (`offsets` such as `{"5": 2}`). Ask if they haven't said.
2. **Measure each scene, one at a time.** A tool call can't talk to the player while it records. Tell them first, for
   example: "Play scene 3 the way you would live, for about 15 seconds. I'll start listening now." Then call
   `measure_level` with that scene. Ask for the same kind of part each time, because a clean arpeggio and a palm-muted
   riff measure differently.
3. **Level.** Call `level_scenes` with every measurement.
   - `peak` moves the preset's level target; `keepPresetLevel: true` only balances the scenes.
   - It notes any trim that hit its limit (fix that scene's amp or block level instead) and any peak that would clip.
   - Use `apply: false` first if the user wants to see the plan.
4. **Check.** Re-measure a scene or two.

## Keeping it

Levels are live edits: `undo` takes them back, and `store_edit_buffer` stores them, through the user's safeguards and
at their own risk (see the fractone-operations skill on backups before storing).

## Notes

- Measurement uses the FM9's USB Output 1 (channels 1-2), so it measures what goes to the player's speakers.
- Loudness is integrated loudness (ITU-R BS.1770, gated, so pauses don't count).
- Only compare scenes of the same preset. Don't level one song's preset against another's.
