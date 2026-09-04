#!/bin/sh
# Builds port-content/ - the override folder the port loads assets from in preference to the
# game's own Content/.
#
# PORT DEVIATION 18. The 19 shipped effects are MGFX v8 containers and MonoGame 3.8 requires
# v10. The port used to rewrite them IN PLACE in the player's Content/ (with a backup). This
# converts them into a separate folder instead, which the game prefers at load time.
#
# Three things that buys:
#
#   * the game's own assets are never modified. Uninstalling the port is deleting files, not
#     restoring a backup, and Steam's file verification has nothing to repair
#   * there is no conversion step to forget. Unzipping the binaries and running the exe used to
#     die several screens into startup with MonoGame's bare "This MGFX effect is for an older
#     release of MonoGame", because install.ps1 - which did the converting - had been skipped
#   * modders get an asset override folder for free: any .xnb dropped in here wins over the
#     shipped one of the same name
#
# The conversion rewrites the CONTAINER ONLY. Every byte of compiled shader bytecode is copied
# through unchanged, so the shaders are bit-identical to what the studio shipped - see
# tools/MgfxTranscode and PORTING-NOTES.md.
set -e
. "$(dirname "$0")/env.sh"
cd "$UW_REPO"

# Defaults to the PRISTINE Steam content, because that is the only copy guaranteed to still be
# the shipped MGFX v8 - game/Content has been transcoded in place by earlier build steps, and
# converting an already-converted effect is a no-op that would look like success. This script
# only ever READS the source; build/verify-steam-untouched.sh checks that.
SRC=${1:-$UW_STEAM/Content}
OUT=${2:-artifacts/port-content}

[ -d "$SRC" ] || { echo "FATAL: no such content directory: $SRC" >&2; exit 1; }

"$DOTNET" build tools/MgfxTranscode/MgfxTranscode.csproj -c Release -v q --nologo
TOOL=$(find artifacts/bin/MgfxTranscode -name 'mgfxtranscode.exe' -path '*release*' | head -1)
[ -n "$TOOL" ] || TOOL=$(find artifacts/bin/MgfxTranscode -name 'mgfxtranscode.exe' | head -1)
[ -n "$TOOL" ] || { echo "FATAL: mgfxtranscode.exe not found" >&2; exit 1; }

rm -rf "$OUT"; mkdir -p "$OUT"

HASHES=$(mktemp)
trap 'rm -f "$HASHES"' EXIT

# Only the effects are copied. Everything else in Content/ loads as shipped, and duplicating it
# here would waste hundreds of megabytes and create two copies to keep in step.
echo "==> collecting effects from $SRC"
COUNT=0
# find | while would run the loop in a subshell and lose COUNT, and `for f in $(find ...)` splits
# on the spaces in "C:\Program Files (x86)\Steam\...". A file list read by redirection avoids
# both.
LIST=$(mktemp)
find "$SRC" -name '*.xnb' -print | sort > "$LIST"
while IFS= read -r f; do
  # An effect .xnb names EffectReader near the top. Match the FULL reader name: "EffectReader"
  # is a substring of "SoundEffectReader", and matching loosely would drag in all 220 sounds.
  if head -c 4096 "$f" | tr -d '\0' | grep -q 'Microsoft.Xna.Framework.Content.EffectReader'; then
    rel=${f#"$SRC"/}
    mkdir -p "$OUT/$(dirname "$rel")"
    cp -p "$f" "$OUT/$rel"
    printf '%s  %s\n' "$(sha256sum "$f" | cut -d' ' -f1)" "$rel" >> "$HASHES"
    COUNT=$((COUNT + 1))
  fi
done < "$LIST"
rm -f "$LIST"
echo "    $COUNT effect(s)"

echo "==> converting the containers to MGFX v10"
"$UW_REPO/$TOOL" transcode "$OUT" 2>&1 | tail -2
"$UW_REPO/$TOOL" validate  "$OUT" 2>&1 | grep -vE 'skinFX_0' | tail -2

# The source must be left exactly as it was - that is the entire point of this deviation - and
# the way to establish that is to hash it before and after, not to compare it with the output.
#
# An earlier version of this check asserted that each output DIFFERS from its source, on the
# reasoning that v8 -> v10 changes the container. That is wrong: it fails on a source that is
# already v10, where copying and converting correctly produces an identical file. It tested the
# wrong invariant.
echo "==> confirming $SRC was not modified"
CHANGED=0
LIST=$(mktemp)
find "$OUT" -name '*.xnb' -print | sort > "$LIST"
while IFS= read -r f; do
  rel=${f#"$OUT"/}
  before=$(grep -F "  $rel" "$HASHES" | cut -d' ' -f1)
  after=$(sha256sum "$SRC/$rel" | cut -d' ' -f1)
  if [ "$before" != "$after" ]; then
    echo "    ! MODIFIED SOURCE: $rel"
    CHANGED=$((CHANGED + 1))
  fi
done < "$LIST"
rm -f "$LIST"
[ "$CHANGED" -eq 0 ] \
  && echo "    all $COUNT source effect(s) byte-identical to before" \
  || { echo "FATAL: the conversion wrote to $SRC - $CHANGED file(s)" >&2; exit 1; }

mgfx_version() {
  off=$(grep -abo 'MGFX' "$1" | head -1 | cut -d: -f1)
  [ -n "$off" ] || { echo "?"; return; }
  od -An -tu1 -j $((off + 4)) -N1 "$1" | tr -d ' '
}
if [ -f "$SRC/multiTex.xnb" ] && [ -f "$OUT/multiTex.xnb" ]; then
  SRCV=$(mgfx_version "$SRC/multiTex.xnb")
  OUTV=$(mgfx_version "$OUT/multiTex.xnb")
  echo "    source multiTex.xnb is still MGFX v$SRCV (untouched)"
  echo "    output multiTex.xnb is MGFX v$OUTV"
  [ "$SRCV" = "8" ] || echo "    note: the source was already v$SRCV, not the shipped v8"
fi

echo
echo "==> $OUT  ($(du -sh "$OUT" | cut -f1))"
echo "Ships as port-content/ beside the executable. The game's Content/ is never written to."
