# Unclaimed World — .NET 8 port, build-from-source kit

Builds a .NET 8 / MonoGame 3.8.5.1 port of *Unclaimed World* from **your own copy** of the game.

## This kit contains no game code and no game assets

That is the point of it, not a limitation. The port is a derivative work of the game, and the
developer — Refactored Games OÜ, in liquidation since December 2021 — has not been reachable for
permission. So nothing of theirs is distributed here. What you get is the **difference**:

| | |
|---|---|
| `patches/` | 38 files' worth of changes: **+1,276 lines of our code**, 341 lines of context |
| `newfiles/` | 13 source files written from scratch |
| `projects/` | the `.csproj` / props files (the studio's VS2013 projects never shipped) |
| `tools/` | our own tools: shader transcoder, content probe, data exporter, proxy generator |
| `orchestrator/` | `uwkit` - the Go source for the single-binary build orchestrator |
| `scripts/` | the PowerShell reference implementation |

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

Download `uwkit.exe` and the kit zip from
[Releases](https://github.com/masaykh/unclaimedworld_modding/releases), unpack somewhere with a
few GB free, and run:

```
uwkit -check      report what is needed, change nothing
uwkit             do it
```

`uwkit.exe` is a single static binary. **No PowerShell, no `cmd` scripting, no `sh`, no GNU
`patch`, no `git`** — which matters because PowerShell is disabled by policy on plenty of
machines. The only external tool it needs is `dotnet`.

`-check` tells you what is missing, what can be installed locally, and how many MB it would
download, without touching anything. The real run asks before each download.

Then run `port\UnclaimedWorld.exe`.

### Options

```
-game <path>       your installation, if it is not found automatically
-assets auto|link|copy|none    how the game's Content/data get into the build
-no-harmony        build without the Harmony mod loader
-no-mod            build without the bundled Unhidden Mod (CHANGES SAVE COMPATIBILITY)
-no-animation      no animated menu background
-y                 accept the prompts (non-interactive)
```

A feature left out is **not compiled in** — not merely disabled. `-no-harmony` produces a build
that references no HarmonyLib and ships no `0Harmony.dll` at all, so nothing can load third-party
code into the process.

### Assets

`-assets auto` tries a directory junction for `Content\` — instant, no copy, no administrator
rights — and offers a copy instead if the filesystem cannot hold reparse points (exFAT and FAT32
cannot). Support is established by *creating* a junction, not by reading the filesystem name.

Only what the game **writes** to is copied; everything else is linked. Measured, that is one
directory:

```
data\BaseData      1 KB   --export-data writes here     -> copied
data\Maps        102 MB   read-only                     -> junction
Content          317 MB   read-only (the converted shaders live in port-content\)  -> junction
```

So a linked build is about **14 MB** on disk, and nothing can write into your game folder.

### The PowerShell scripts

`setup-port.ps1` and `scripts/` are the original implementation and still work:

```
powershell -ExecutionPolicy Bypass -File setup-port.ps1 -CheckOnly
```

They are kept as a reference — having two independent implementations of the patch applier is how
a hunk-boundary bug in it was found — but `uwkit.exe` is the one to reach for. It also does two
things the scripts cannot: it reads the game's `FileVersion` through `version.dll` (`cmd` has no
way to do this at all now that `wmic` is gone from Windows 11), and it applies patches without
needing GNU `patch` installed.

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

The code in `patches/`, `newfiles/`, `projects/`, `tools/`, `scripts/` and `orchestrator/` is MIT
(see `LICENSE`); third-party components are listed in `THIRD-PARTY-NOTICES.md`. The
game's code and assets are Refactored Games' and are not included. You need your own legally
obtained copy. If you are Refactored Games, or can reach them, we would very much like to hear
from you.

## Contributing patches

Two lanes, and which one you want depends on whether you are changing a file or adding one.

**`newfiles/extra/src/<Project>/...`** — whole files. A mod that is a new `.cs` file goes here,
laid out the way `newfiles/` is. Nothing to diff against, nothing to keep in step with a game
version.

**`patches/extra/<Project>/*.patch`** — changes to a file that already exists. Applied in
filename order, so prefix them (`10-`, `20-`) when order matters. Generate one with `diff -U1`
against the tree in `work/src/<Project>/`.

Both run **after** the kit's own patch series and after `newfiles/` has been copied in — so an
extra patch can target the port's own files (`UWGame/Mods/...`) and not only the decompiled ones.
That ordering was the other way round until it was found to matter: a patch against a port file
reported *applied* and was then overwritten by the `newfiles/` copy, silently, and on a clean
`work/` the same patch failed with *missing target* instead. If you hit either symptom on an older
kit, that is what it was.

A patch that fails to apply stops the build and names itself, so a broken third-party patch is
never mistaken for the kit being broken.

That is what makes this extensible without a release: drop in a file, or write a diff, and it
composes with everything else.

**If you have the repository rather than the kit, do not hand-write patches at all.** Edit `src/`
and open a PR; `build/90-make-patch-kit.sh` regenerates the entire series from the difference
against `decomp/`. The `extra` lanes exist for people who have this kit and not the source.
