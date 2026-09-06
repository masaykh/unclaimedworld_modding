#!/bin/sh
# Converts the main-menu background video into MainMenuIntro.uwanim - a motion-JPEG frame
# sequence the game plays without any video decoder.
#
# WHY. The menu background is Content/MainMenu/TauCetiMainMenu.wmv, played through MonoGame's
# MediaFoundation-backed VideoPlayer. That single asset is responsible for a lot of weight:
# MonoGame has no DesktopGL VideoPlayer at all (so the GL build has no menu animation), the game
# gates startup on a Windows Media Player registry check, and three port deviations (5, 7 and 11)
# exist purely to work around VideoPlayer behaviour. A frame sequence removes all of it.
#
# WHY MOTION JPEG, having measured the alternatives on this exact clip at 640x360 / 12 fps:
#
#   motion JPEG   3.1 MB   MonoGame's ILMerged StbImageSharp already decodes JPEG - NO new
#                          dependency, and it decodes on every backend, so GL gets the
#                          animation too
#   WebP          1.6 MB   smallest, but needs SixLabors.ImageSharp, which is under the Six
#                          Labors split licence (commercial terms above a revenue threshold) -
#                          the wrong thing to put in a kit handed to strangers
#   APNG         38.2 MB   works with no new dependency in principle, since APNG frames are
#                          ordinary zlib PNG data that can be rewrapped for the stb decoder -
#                          but lossless is hopeless on photographic gradients, and this is FOUR
#                          TIMES the size of the video it replaces
#   AVIF / JXL      n/a    no managed decoder exists; both need native libavif/dav1d/libjxl
#
# The output is written to the GAME ROOT, not into Content/. The original .wmv and every other
# shipped asset stay exactly as they are - this is an additional file beside the executable, so
# nothing the studio shipped is modified and the change is undone by deleting one file.
#
# Container layout (little-endian), documented here and parsed by UWGame.Port.MenuAnimation:
#
#   0   char[8]   "UWANIM01"
#   8   int32     version (1)
#   12  int32     frame width
#   16  int32     frame height
#   20  int32     frame count
#   24  int32     frame delay, milliseconds
#   28  int32[n]  length in bytes of each JPEG frame
#   ..            the JPEG frames, back to back
#
# A trivial container rather than a real one because the frames must reach Texture2D.FromStream
# as standalone JPEGs, and one file keeps the game folder clean.
set -e
. "$(dirname "$0")/env.sh"
cd "$UW_REPO"

WIDTH=${UW_ANIM_WIDTH:-854}
HEIGHT=${UW_ANIM_HEIGHT:-480}
FPS=${UW_ANIM_FPS:-12}
QUALITY=${UW_ANIM_Q:-4}          # ffmpeg -q:v, 2..31, lower is better
SRC=${1:-game/Content/MainMenu/TauCetiMainMenu.wmv}
OUT=${2:-game/MainMenuIntro.uwanim}

FFMPEG=$(command -v ffmpeg 2>/dev/null || true)
[ -n "$FFMPEG" ] || FFMPEG=$(find "/c/Users/$USERNAME/AppData/Local/Microsoft/WinGet/Packages" -name 'ffmpeg.exe' 2>/dev/null | head -1)
[ -n "$FFMPEG" ] || FFMPEG=$(find /c/Users/*/AppData/Local/Microsoft/WinGet/Packages -name 'ffmpeg.exe' 2>/dev/null | head -1)
[ -n "$FFMPEG" ] || { echo "FATAL: ffmpeg not found. Install with: winget install Gyan.FFmpeg" >&2; exit 1; }

[ -f "$SRC" ] || { echo "FATAL: no such video: $SRC" >&2; exit 1; }

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

echo "==> ffmpeg:  $FFMPEG"
echo "==> source:  $SRC ($(du -h "$SRC" | cut -f1))"
echo "==> frames:  ${WIDTH}x${HEIGHT} @ ${FPS}fps, jpeg -q:v $QUALITY"

"$FFMPEG" -nostdin -v error -y -i "$SRC" \
  -vf "fps=$FPS,scale=$WIDTH:$HEIGHT" -pix_fmt yuvj420p -q:v "$QUALITY" "$TMP/%05d.jpg"

COUNT=$(ls "$TMP"/*.jpg 2>/dev/null | wc -l | tr -d ' ')
[ "$COUNT" -gt 0 ] || { echo "FATAL: ffmpeg produced no frames" >&2; exit 1; }

DELAY=$((1000 / FPS))
mkdir -p "$(dirname "$OUT")"

# Pack. Perl rather than a C# tool so this stays runnable before anything is built.
perl -e '
  use strict; use warnings;
  my ($dir, $out, $w, $h, $delay) = @ARGV;
  opendir(my $dh, $dir) or die "opendir: $!";
  my @files = sort grep { /\.jpg$/ } readdir($dh);
  closedir($dh);
  my @blobs;
  for my $f (@files) {
    open(my $fh, "<", "$dir/$f") or die "open $f: $!";
    binmode($fh); local $/; my $d = <$fh>; close($fh);
    push @blobs, $d;
  }
  open(my $o, ">", $out) or die "open $out: $!";
  binmode($o);
  print $o "UWANIM01";
  print $o pack("l<5", 1, $w, $h, scalar(@blobs), $delay);
  print $o pack("l<*", map { length($_) } @blobs);
  print $o $_ for @blobs;
  close($o);
' "$TMP" "$OUT" "$WIDTH" "$HEIGHT" "$DELAY"

SIZE=$(stat -c%s "$OUT")
SRCSIZE=$(stat -c%s "$SRC")
echo "==> wrote:   $OUT"
awk -v n="$COUNT" -v s="$SIZE" -v v="$SRCSIZE" -v d="$DELAY" 'BEGIN{
  printf "    %d frames, %d ms each (%.1f s), %.2f MB - the .wmv is %.2f MB (%.0f%%)\n",
    n, d, n*d/1000, s/1048576, v/1048576, 100*s/v }'
echo
echo "The original video and all shipped Content/ assets are untouched; this is an extra file"
echo "in the game root. Delete it and the game falls back to the still background image."
