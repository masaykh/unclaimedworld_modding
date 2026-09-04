# Third-party notices

The code in this repository is MIT-licensed (see `LICENSE`). It builds against, and at build time
fetches, the following third-party components. Licences verified from each package's own metadata
rather than from memory.

| Component | Licence | How it is used |
|---|---|---|
| MonoGame.Framework.WindowsDX | **MS-PL** | NuGet reference; the port's framework |
| Lib.Harmony (HarmonyLib) | MIT | NuGet reference; the mod loader |
| Steamworks.NET | MIT | NuGet reference; Steam integration |
| SharpDX (+ DXGI, Direct2D1, Direct3D11, MediaFoundation, XAudio2, XInput) | MIT | pulled in by MonoGame's WindowsDX backend |
| ilspycmd / ICSharpCode.Decompiler | MIT | local dotnet tool; decompiles your own copy |
| Mono.Cecil | MIT | tooling |
| Go standard library | BSD-3-Clause | `uwkit` is built with it, stdlib only |
| **ffmpeg** | **GPL** | see below |
| Steamworks SDK native (`steam_api64.dll`) | Valve Steamworks SDK terms | see below |

## ffmpeg is GPL, and is deliberately not bundled

`uwkit` can download a portable ffmpeg **onto your machine** and **invokes it as a separate
process** to extract frames for the optional menu-background animation.

That arrangement matters. Running a GPL program as a separate process is not linking, so ffmpeg's
copyleft does not extend to this repository's code. **Do not bundle ffmpeg into the kit or attach
it to a release** — that would change the analysis, and it would also collide with MonoGame's
MS-PL, which is not GPL-compatible.

ffmpeg is never required. Without it the game uses its still menu background.

## The Steam native library is not redistributed

`steam_api64.dll` is a Valve Steamworks SDK redistributable. This repository does not contain it.

`uwkit` first checks the copy in **your own game installation** and uses it if it exports what the
current Steamworks.NET needs. Game 1.0.4.8 ships Steamworks SDK ~1.34, which does not — it is
missing `SteamInternal_SteamAPI_Init`, `SteamInternal_CreateInterface` and
`SteamAPI_ManualDispatch_Init`, all of which Steamworks.NET P/Invokes by name. In that case, and
only after asking, it downloads the matching native from the Steamworks.NET release onto your
machine.

Achievements are optional: without it the game runs and logs that they are unavailable.

## Unclaimed World itself

The game's code and assets are the property of **Refactored Games OÜ** and are **not** contained
in this repository in any form. You need your own legally obtained copy.

The `patches/` directory contains unified diffs. Diff *context* lines necessarily quote the
original — 265 lines across the whole series, against a codebase of roughly 15 MB. Everything
else in those patches is our own added code.
