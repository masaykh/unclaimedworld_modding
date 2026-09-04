# Unclaimed World — .NET 8 port, build-from-source kit

Builds a .NET 8 / MonoGame 3.8.5.1 port of *Unclaimed World* from **your own copy** of the game.

## This kit contains no game code and no game assets

That is the point of it, not a limitation. The port is a derivative work of the game, and the
developer — Refactored Games OÜ, in liquidation since December 2021 — has not been reachable for
permission. So nothing of theirs is distributed here. What you get is the **difference**:

| | |
|---|---|
| `patches/` | 32 files' worth of changes: **+747 lines of our code**, 265 lines of context |
| `newfiles/` | 12 source files written from scratch |
| `projects/` | the `.csproj` / props files (the studio's VS2013 projects never shipped) |
| `tools/` | our own tools: shader transcoder, content probe, data exporter, proxy generator |
| `scripts/` | the setup and build orchestration |

Everything of the studio's — 1,496 unchanged source files, all art, audio, text, shaders — comes
off **your** installation, decompiled on **your** machine. Your game folder is only ever read.

## Requirements

- **.NET SDK 8** — <https://dotnet.microsoft.com/download/dotnet/8.0>. The one thing that cannot
  be installed locally.
- **Your own installed copy of Unclaimed World, version 1.0.4.8.** The patches are generated
  against that exact version and the setup refuses a mismatch, because a mismatch fails later at
  content load with a bare cast exception that names no version.
- Optional: `ffmpeg`, only for the animated menu background. The kit can fetch a portable copy
  into `prereqs\`; without it the menu uses the still image.

Everything else — the ILSpy decompiler, the MonoGame content tools — is fetched as a **local**
dotnet tool. Nothing goes into Program Files, the registry, the PATH, or the global tool store.
Deleting this folder removes everything except the SDK.

## Use it

```
powershell -ExecutionPolicy Bypass -File setup-port.ps1 -CheckOnly    report, change nothing
powershell -ExecutionPolicy Bypass -File setup-port.ps1              do it
```

`-CheckOnly` tells you what is missing, what can be installed locally, and how many MB it would
download, without touching anything. The real run asks before each of those.

Then run `port\UnclaimedWorld.exe`.

### Options

```
-Harmony       yes|no    the Harmony mod loader (loads user/Mods/*.dll)
-UnhiddenMod   yes|no    bundled community mod: adds items/recipes. CHANGES SAVE COMPATIBILITY
-MenuAnimation yes|no    build the animated menu background (needs ffmpeg)
-Assets        auto|link|copy|none    how the game's Content/data get into the build
-Game <path>   your installation, if it is not found automatically
```

A feature set to `no` is **not compiled in** — not merely disabled. `-Harmony no` produces a
build that references no HarmonyLib and ships no `0Harmony.dll`, so nothing can load third-party
code into the process.

### Assets

`-Assets auto` tries a directory junction for `Content\` — instant, no copy, no administrator
rights — and falls back to offering a copy if the filesystem cannot hold reparse points (exFAT
and FAT32 cannot). Support is established by *creating* one, not by reading the filesystem name.

`data\` is **always a real copy**, never a link: `--export-data` writes into `data\BaseData\`, and
a link would put that inside your real game folder.

## What it does, and how far it is verified

The build runs nine steps: decompile your six managed assemblies, lay them out as six projects,
apply the patch series, add our files, bootstrap and generate the XML serialization proxies
(two-phase — the generator reflects over the built game, which cannot compile without the proxies
it produces), build with your chosen features, convert the 19 shaders, optionally build the menu
animation, and stage the output.

**Verified end to end on game 1.0.4.8:** the decompile reproduces the same file counts
(1523/99/5/14/6/34), all six patch series apply with zero problems, 28 proxies generate,
the build succeeds, 19 shaders convert, and the result loads content — 14/14 assets including a
song and two effects, with the shader startup check silent.

**Not verified:** that the resulting binaries are **byte-identical** to ours. `Deterministic` and
`PathMap` are both set, and the generated proxies are reproducible (28/28 identical), but the
assemblies still hash differently and the remaining cause is not yet identified. The build is
functionally equivalent — same source, same SDK (`global.json` pins 8.0.420), a working game —
but do not expect matching hashes yet.

**Also not verified by any automated gate:** rendering and gameplay. Content, media, shaders and
the mod loader are checked; how it looks and plays needs a person.

## Your game files are never modified

The 19 shaders need converting from MGFX v8 to v10 for MonoGame 3.8, and the converted copies go
in `port-content\`, which the game loads *in preference to* `Content\`. Your originals stay
untouched — if you inspect them and find v8, that is correct. Anything you drop in
`port-content\` overrides the shipped asset of the same name, which is a free mod hook for any
content type.

## Licensing

The code in `patches/`, `newfiles/`, `projects/`, `tools/` and `scripts/` is ours to share. The
game's code and assets are Refactored Games' and are not included. You need your own legally
obtained copy. If you are Refactored Games, or can reach them, we would very much like to hear
from you.

## uwkit.exe — the single-binary orchestrator

`uwkit.exe` does everything the PowerShell scripts do, with **no PowerShell, no `cmd` scripting,
no `sh`, no GNU `patch` and no `git`**. It is a 6.7 MB static binary with no runtime dependency;
the only external tool it needs is `dotnet`, which is unavoidable.

```
uwkit                 check, ask, then build
uwkit -check          report what is needed and change nothing
uwkit -y              accept the prompts

-game <path>          your installation, if it is not found automatically
-assets auto|link|copy|none
-no-harmony           build without the Harmony mod loader
-no-mod               build without the bundled Unhidden Mod
-no-animation         no animated menu background
```

This exists because PowerShell is disabled by policy on plenty of machines. The `.ps1` scripts
are kept as the reference implementation and still work, but `uwkit.exe` is the one to reach for.

Two things it does that a script cannot do well: it reads the game's `FileVersion` through
`version.dll` (cmd has no way to do this at all — `wmic` is gone from Windows 11), and it
establishes junction support by *creating* one rather than guessing from the filesystem name.
