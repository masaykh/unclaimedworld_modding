#!/bin/sh
# Regenerates src/UnclaimedWorld/Generated/XmlProxies/ - the XML serialization proxy types
# CustomXmlSerializer used to build at runtime with CodeDom (PORT DEVIATION 12).
#
# Usage:
#   build/20-generate-xml-proxies.sh            regenerate the files
#   build/20-generate-xml-proxies.sh --check    fail if what is committed is out of date
#
# There is a bootstrap order here, which is the whole reason this is a script and not an
# MSBuild target: the generator reflects over the BUILT UnclaimedWorld.dll, and its output is
# compiled INTO that same assembly. So:
#
#   1. build the game with whatever proxies are currently committed
#   2. run the generator against that build
#   3. build again, so the regenerated proxies are the ones in the binary
#
# Step 1 works even when a proxy is stale because the proxies are additive - nothing in the
# game reads them until an XML file is actually deserialized, and the generator only needs the
# proxied types' shape, which comes from the hand-written source, not from the proxies.
#
# --check is the guard worth wiring into any release build: a proxied type can gain a field
# without anyone remembering to regenerate, and the failure mode of a stale proxy is silent -
# the field simply vanishes from the XML instead of erroring.
set -e
. "$(dirname "$0")/env.sh"
cd "$UW_REPO"

MODE=${1:-}
OUT=src/UnclaimedWorld/Generated/XmlProxies
BIN=artifacts/bin/UnclaimedWorld/release_dx

mkdir -p "$OUT"

# Bootstrap. Step 1 cannot compile if the registry is absent, because CustomXmlSerializer
# references it directly - which is deliberate, so a missing proxy is a build error rather
# than a first-time-you-load-a-mod error. That does mean deleting the generated directory
# would otherwise be unrecoverable, so seed an empty registry and let the real one overwrite
# it in step 2.
if [ ! -f "$OUT/XmlProxyRegistry.g.cs" ]; then
  echo "==> no registry present; seeding an empty one to bootstrap the build"
  cat > "$OUT/XmlProxyRegistry.g.cs" <<'STUB'
// Placeholder registry, written by build/20-generate-xml-proxies.sh to bootstrap a tree with
// no generated proxies. It is overwritten with the real thing later in the same run; if you
// are reading this in a committed file, the generator did not get that far.

using System;
using System.Collections.Generic;

namespace UWGame.Generated.XmlProxies;

internal static class XmlProxyRegistry
{
	internal static readonly Dictionary<Type, Type> ProxyTypes = new();
}
STUB
fi

echo "==> building the game (DX Release) so the generator has something to reflect over"
"$DOTNET" build src/UnclaimedWorld/UnclaimedWorld.csproj -c Release -p:UwPlatform=DX -v q --nologo

echo "==> building the generator"
"$DOTNET" build tools/XmlProxyGen/XmlProxyGen.csproj -c Release -v q --nologo \
  -p:UwGameBin="$(pwd -W 2>/dev/null || pwd)/$BIN"

GEN=$(find artifacts/bin/XmlProxyGen -name 'xmlproxygen.exe' -path '*release*' | head -1)
[ -n "$GEN" ] || GEN=$(find artifacts/bin/XmlProxyGen -name 'xmlproxygen.exe' | head -1)
[ -n "$GEN" ] || { echo "FATAL: xmlproxygen.exe not found" >&2; exit 1; }

# The generator LOADS the game assembly, so it needs the real runtime assemblies beside it -
# not the reference assemblies that land in the build output. Steamworks.NET is the one that
# actually differs: the copy at the output root is a reference assembly and throws
# "Cannot load a reference assembly for execution", while the usable one sits under
# runtimes/win-x64/. Same root cause as the note in build/40-deploy.sh.
GENDIR=$(dirname "$GEN")
cp -p "$BIN"/*.dll "$GENDIR"/ 2>/dev/null || true
RT=$(find artifacts/publish/UnclaimedWorld/release_dx/runtimes/win-x64 -name 'Steamworks.NET.dll' 2>/dev/null | head -1)
if [ -n "$RT" ]; then
  cp -p "$RT" "$GENDIR"/
else
  echo "    ! no runtime Steamworks.NET found; run a publish first if the generator fails to load types" >&2
fi

mkdir -p "$OUT"
if [ "$MODE" = "--check" ]; then
  echo "==> checking $OUT against the current types"
  "$GEN" "$OUT" --check
  exit $?
fi

echo "==> generating into $OUT"
"$GEN" "$OUT"

echo "==> rebuilding with the regenerated proxies"
"$DOTNET" build src/UnclaimedWorld/UnclaimedWorld.csproj -c Release -p:UwPlatform=DX -v q --nologo

echo "==> verifying the regenerated files are self-consistent"
"$GEN" "$OUT" --check
