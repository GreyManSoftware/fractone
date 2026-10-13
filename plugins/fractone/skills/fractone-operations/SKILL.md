---
name: fractone-operations
description: Read, back up, edit, audition, store and restore presets on a Fractal Audio FM9 through the fm9 MCP tools. Use when the user asks what a preset contains, wants a preset changed (models, settings, channels, scenes, blocks, name), wants to hear or load a preset, or wants presets backed up, stored or restored.
---

# FM9 preset operations

The fm9 tools reach a Fractal Audio FM9 connected to this computer by USB. Presets travel as `.syx` file paths. A
relative path means a file in the user's work folder (`status` shows where that is).

## Start here

Call `status`. It shows the firmware, the preset and scene showing, the MIDI ports and the safeguards the user
has chosen. If it reports no FM9, say so and stop device work. Offline edits still work without the FM9.

## How the FM9 is organised

- **Presets** live in slots 0-511. Each preset has a grid of blocks and up to 8 **scenes**.
- **Blocks** are named by type and instance: `Amp`, `Amp 2`, `Cab`, `Drive`, `Gate/Expander`, `Delay`,
  `Parametric EQ` and so on.
- Most blocks have **channels A-D**, each a complete set of settings. A scene picks a channel for every block and
  whether the block is bypassed.
- The **edit buffer** is what the FM9 is playing now, including unsaved changes. Loading a preset into it does not
  store anything.

## Reading

- `describe_preset` on a file, or `read_preset` for a slot, lists every block and what each channel plays,
  with the scenes that use it. Describe a preset in those terms: which amp and cab each scene plays, and what is
  bypassed.
- `get_channel` gives every setting of one block channel. `list_block_settings` gives the settings a block
  type has, with ranges and choices (every amp model name, for instance).
- `read_edit_buffer` captures what is playing now, unsaved edits included.

## Editing

Edits never touch the FM9 and never overwrite their input. Each writes a new file and reports what changed. Chain
them by passing each result's `output` to the next edit.

- `set_channel` writes only the settings given. Choosing a model by name (`"Amp Model": "USA IIC++"`) loads that
  model's FM9 voicing first, as selecting it on the FM9 does, so set the model and then the knobs.
- `copy_channel` copies a channel within a preset or from another preset.
- `compose_scene` sets a scene's channels, bypass states and name, optionally starting from a copy of another scene.
- `add_block` puts a new block into a cell holding a shunt. `remove_blocks` leaves a shunt so cables stay.
  Other routing changes are made on the FM9 itself.
- `rename_preset` renames.

## Live control

To change what the FM9 is playing right now, as FM9-Edit does, use the live tools. They change only the edit buffer:

- `get_parameters` reads a block's live settings, its channel and its bypass state.
- `set_parameters` turns knobs: one or more settings of a block, read back as old -> new. Naming a model or type
  (`"Amp Model": "Brit 800"`) makes the FM9 load its voicing first. Naming a channel other than the one playing
  switches the block to it in the current scene, as clicking a channel in FM9-Edit does.
- `set_block` switches a block's channel or bypass in the current scene. `select_scene` changes scene and keeps
  unsaved edits.
- `store_edit_buffer` keeps the result: it stores what is playing into a slot, through the same checks as any store.

Experimenting is safe:

- `history` lists the live edits on the preset showing.
- `undo` takes them back exactly, newest first. A model change is undone by restoring the edit buffer from just
  before it.
- `snapshot` keeps the whole sound under a name, and `restore_snapshot` brings it back. Both can be undone.

History starts again when another preset is selected or the edits are stored.

## Comparing

`diff` compares any two of: a `.syx` file, a slot number, `"live"` (what is playing now) or `"last-load"`. It lists
settings by block and channel in display units, scene changes and grid changes. For example, `"505"` against `"live"`
answers "what have I changed?". `soundOnly` hides names and channels no scene plays. Every store also reports what it
changes in the slot, so show that to the user before confirming.

Live edits are lost if the preset is changed before they are stored, so say so when the user seems happy with a sound.
Use the offline edits instead for changes to presets that aren't playing, or for building a file to keep.

## Listening and storing

- `audition` loads a file into the edit buffer so the user can play it. Nothing is stored, and switching presets
  on the FM9 discards it. Pass a `slot` to select that preset first; that slot's own unsaved edits are lost.
- `store_preset` writes a file into a slot and reports what it replaced. A store can only be undone by storing the
  old preset back, so make sure the user wants that slot. If the result's status is `confirm`, the user's settings
  require confirmation: show the user the message, and call again with the code only once they agree.
- To **restore**, store the backup file. Stores keep backups in the work folder's `backups` folder when that
  safeguard is on.
- `backup` copies a range of slots to files. A full backup of 512 presets takes about 2-3 minutes.

## When something fails

Tool errors say what went wrong. Common ones:

- **"The FM9's MIDI ports could not be opened."** FM9-Edit or another program holds the ports. Ask the user to
  disconnect FM9-Edit.
- **"Not writable under the current settings."** The user limited the writable slots. Don't work around it; ask.
- **"The edit buffer has changes that are not stored."** The user has unsaved edits on the FM9. Ask them what to do.
- **Firmware other than 12.** Writes are refused because these tools know firmware 12's format. Reading still works.

Report results plainly: which file was written, which slot changed and what it replaced.
