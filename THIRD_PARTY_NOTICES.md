# Third-party notices

## FM9 community parameter catalog and Gen-3 block IDs

`src/Fractal.Fm9/Catalog/fm9-12-community-beta.json` is copied from
[`monzta1/ToneCommand`](https://github.com/monzta1/ToneCommand) commit
`c2c8a8789b0f065e2a5d37d0b359fbb33d28fdb1`, file
`config/fm9_catalog.json` (blob `f0664b65a84fba9170db4303acc99c6454288bc5`).
ToneCommand is licensed under Apache License 2.0.

ToneCommand identifies this data as a verbatim copy of the FM9 catalog from
[`TheAndrewStaker/mcp-midi-control`](https://github.com/TheAndrewStaker/mcp-midi-control),
where it is available as
`packages/fractal-midi/catalog/fm9.json`. The catalog contains community-derived
FM9 parameter definitions and is **community-beta**, not an official Fractal
Audio catalog. This project pins it for FM9 firmware 12 and refuses entries
that the catalog marks `unverified`.

The firmware-12 standard block-ID roster in
`Fm9Firmware12Catalog.cs` is derived from Fractal Audio's public *Axe-Fx III
MIDI for 3rd Party Devices*, Revision 1.4, Appendix 1, and cross-checked
against `mcp-midi-control` commit
`59047175cfc4f23e092931b54a7c54f2bffde3ea`, file
`packages/fractal-midi/src/gen3/axe-fx-iii/blockTypes.ts`.

MCP MIDI Control
Copyright 2026 Stephen Staker

The Apache License 2.0 text is included at
`third_party/LICENSES/Apache-2.0.txt`.

“Fractal Audio”, “Axe-Fx”, “FM3”, and “FM9” are trademarks of Fractal Audio
Systems, Inc. This project is an unaffiliated community tool and is not
endorsed by Fractal Audio Systems.

## Audio decoders

`AudioFiles` reads OGG Vorbis with [NVorbis](https://github.com/NVorbis/NVorbis) 0.10.5 (MIT License, Copyright Andrew
Ward) and Opus with [Concentus](https://github.com/lostromb/concentus) 2.2.2 and Concentus.Oggfile 1.0.7 (BSD-3-Clause,
a C# port of the Opus reference codec, Copyright Xiph.Org Foundation, Skype and others; port by Logan Stromberg). Their
licence texts come with their NuGet packages. MP3, AAC/M4A and FLAC are decoded by Windows Media Foundation.

## NAudio.Midi

The Windows-only MIDI transport references
[`NAudio.Midi` 2.2.1](https://www.nuget.org/packages/NAudio.Midi/2.2.1).

Copyright 2020 Mark Heath

NAudio.Midi is licensed under the MIT License. Its licence text is distributed
with the NuGet package. The transport is not registered or instantiated
automatically; constructing it explicitly opens the selected Windows MIDI
ports.

## System.IO.Ports

The FM3's USB serial transport references [`System.IO.Ports` 10.0.0](https://www.nuget.org/packages/System.IO.Ports/10.0.0)
(with its `runtime.*.runtime.native.System.IO.Ports` packages on macOS and Linux), part of .NET.

Copyright (c) .NET Foundation and Contributors

System.IO.Ports is licensed under the MIT License. Its licence text is distributed with the NuGet package. A serial
port is opened only when the FM3 transport is constructed explicitly.

## Avalonia (probe kit GUI)

`fractone-probe-gui` is built with [Avalonia](https://github.com/AvaloniaUI/Avalonia) 12.1.3 (`Avalonia`,
`Avalonia.Desktop`, `Avalonia.Themes.Fluent`), MIT License, Copyright The Avalonia Project (AvaloniaUI OÜ and
contributors). It brings in SkiaSharp and HarfBuzzSharp (MIT, Copyright Microsoft Corporation and Xamarin), MicroCom
(MIT), ANGLE on Windows (BSD-3-Clause, Copyright The ANGLE Project Authors) and, on Linux, Tmds.DBus (MIT). Their licence texts come with their NuGet packages.

## Stem separator add-on (downloaded on the user's machine, not distributed)

The optional `separate` tool installs these on first use into a folder the user chooses. None of them is bundled
in this package; each comes from its publisher under its own licence:

- [uv](https://github.com/astral-sh/uv) 0.12.23 (MIT or Apache-2.0), from its GitHub release, checked against a
  pinned SHA-256 digest; uv then downloads a CPython 3.12 build (PSF License).
- [PyTorch](https://pytorch.org) 2.5.1 and torchaudio (BSD-3-Clause), from PyPI or download.pytorch.org.
- [Demucs](https://github.com/facebookresearch/demucs) 4.0.1 (MIT) and its `htdemucs_6s` model weights, from PyPI
  and Meta's download server.

## Headless browser for `capture_media` (downloaded on the user's machine, not distributed)

On first use, `capture_media` downloads Google's [Chrome for Testing](https://googlechromelabs.github.io/chrome-for-testing/)
headless shell 155.0.8059.39 from Google's storage, checked against the MD5 the storage reports, into a folder the user
chooses. It is not bundled in this package and comes under Chromium's licences (BSD-3-Clause and others).

## NOTICE of `fractal-midi` (reproduced as Apache-2.0 section 4(d) requires)

Our `fm9-12-community-beta.json` is `fractal-midi@0.8.0`'s `catalog/fm9.json`
(github.com/TheAndrewStaker/mcp-midi-control, `packages/fractal-midi`), **modified** on 2026-10-13 as Apache-2.0 section
4(b) requires us to state: we added FM9 firmware-12 entries regenerated from the FM9-Edit firmware-12 dictionary (Amp
models 331-335, Drive 86, and 12 parameters: Chorus Bass, Treble and Delay Range, Delay Dry Diffusion, Fuzz
Oversample, Mixer Delay 1-6, Global MIDI Thru) and renamed Amp 283 to "Deluxe Tweed Bright". Everything else is
unchanged. That package's NOTICE reads, in its relevant parts:

> fractal-midi
> Copyright 2026 Stephen Staker
>
> This product is licensed under the Apache License, Version 2.0.
>
> This library includes gen-3 (Axe-Fx III / FM3 / FM9) protocol data derived from:
>
> - fractal-syx-codec (Apache License, Version 2.0)
>   Copyright 2026 Andrew Mercurio ("BoodieTraps")
>   https://github.com/drewmerc302/fractal-syx-codec
>   The gen-3 preset-file format spec (FORMAT.md) and the read-ordinal effect-type roster tables.

Its trademark statement applies here too: "Fractal Audio", "Axe-Fx", "FM3", "FM9" and related product names are
trademarks of Fractal Audio Systems, Inc. This project is an unaffiliated community tool and claims no endorsement.
