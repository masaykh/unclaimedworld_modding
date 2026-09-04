#!/bin/sh
# Shell environment for this repo. Source it:  . build/env.sh
# Host: Windows 11 x64, msys2 MINGW64 bash. Note none of these are on PATH by default.

UW_REPO="/c/tools/unclaimedworld_fxna"
UW_STEAM="/c/Program Files (x86)/Steam/steamapps/common/Unclaimed World"
UW_GAME="$UW_REPO/game"

DOTNET="/c/Program Files/dotnet/dotnet.exe"
SEVENZIP="/c/Program Files/7-Zip/7z.exe"

PATH="/c/Program Files/dotnet:/c/Program Files/7-Zip:$PATH"
export PATH UW_REPO UW_STEAM UW_GAME DOTNET SEVENZIP

# Never write into $UW_STEAM. It is the golden reference; `game/` is the run target.
