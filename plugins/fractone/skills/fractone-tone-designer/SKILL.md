---
name: fractone-tone-designer
description: Design a new FM9 preset from a tone description (artist, song, album, era or sound) and build it with the FracTone tools. Use when the user wants a preset created, not just edited.
---

# FM9 tone designer

You write the preset as a JSON proposal, and `build_preset` checks and builds it. The build runs the same repairs
and quality checks as the FracTone site and needs no AI service or API key.

## Steps

1. **Read the rules.** Call `tone_design_guide` once per session. It holds the JSON contract and value ranges,
   every amp, drive and cab choice, the effect blocks and modifiers, and notes on how particular models behave on the
   FM9. Follow it exactly: it is the contract the build checks.
2. **Choose the gear.** Prefer the FM9 model of the hardware actually used on that recording or in that era over one
   that merely sounds closer; tone matching closes the gap later. Substitute only when the FM9 has no model of it,
   and say so. Account for the player's guitar and pickups when they mention them.
3. **Label what you know.** The build has no source retrieval, so gear choices are inferences. Classify evidence as
   `inferred` or `substituted` with honest confidence, never as `documented`.
4. **Use scenes and channels** when the request has parts: a clean verse and a heavy chorus, rhythm and lead, or
   different songs. Give each part its own Amp channel when its recorded tone differs, and have each scene pick the
   channels and bypass states it needs.
5. **Set the other controls.** The site has a second step that sets every other control of the chosen models; the offline
   build does not, so the guide's "leave settings empty" does not apply here. Controls the contract lists go in
   `parameters`. Any other control that matters goes in the block's `settings` list, named as
   `list_block_settings` gives it. Anything left out keeps the model's own FM9 voicing.
6. **Build.** Call `build_preset` with the JSON, or with the path of a `.json` file for long proposals.
   - If `passed` is false, fix every item in `corrections` and build again.
   - `adjustments` lists what the checks changed (clamped values, levels, routes). Mention the ones that matter.
7. **Present it.** Describe the result from its summary: each scene with its amp channel, drive, cab and effects.
   Offer `audition` to hear it and `store_preset` to keep it (see the fractone-operations skill: storing is at the
   user's own risk, so suggest a backup first).

## Changing a design

To change a design you are still building, edit the proposal and build again. To change a preset that already exists
as a `.syx`, use the edit tools (`set_channel`, `compose_scene` and so on); a rebuild starts from the
template again.

If the user has a recording of the target tone, the fractone-tone-match skill can fit the EQ once they have played the
preset.
