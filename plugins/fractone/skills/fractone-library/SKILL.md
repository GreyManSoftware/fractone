---
name: fractone-library
description: Save, list and apply FM9-Edit block favourites (amp, cab, drive or any block) with the FracTone tools. Use for "save this amp to my favourites" or putting a saved block into a preset.
---

# FM9-Edit favourites

FM9-Edit keeps favourite blocks as `.blk` files in the user's block library folder (`status` shows where). These
tools read and write that exact format, so favourites saved here appear in FM9-Edit, and FM9-Edit's appear here.

- **List.** `library_list`, optionally for one block type, shows each favourite's name, whether it holds all four
  channels or one, and the model each channel plays.
- **Save.** `library_save` saves a block of a `.syx` preset, all channels or one channel.
  - To save what the FM9 is playing now, call `read_edit_buffer` first and save from that file.
  - To save from a stored preset, call `read_preset` first.
  - Use the name the user gives; FM9-Edit lists favourites by it.
- **Apply.** `library_apply` writes a favourite into a block of the same type in a preset, into a new file. An
  all-channel favourite replaces every channel, or just one when asked. Scenes and cables are untouched. Then
  `audition` it, and `store_preset` if the user wants to keep it (at their own risk; see the fractone-operations
  skill on backups before storing).

FM9-Edit may need its library view refreshed, or a restart, to show a favourite saved while it was open.
