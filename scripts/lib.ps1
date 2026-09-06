# Helpers for build-port.ps1. Dot-sourced, no side effects on import.

# ---------------------------------------------------------------- unified diff applier
#
# WHY NOT `patch`. GNU patch is not present on a stock Windows machine, and neither is git. The
# kit is supposed to work on a plain install with nothing but the .NET SDK, so shelling out to a
# tool the user probably does not have would defeat the point. This applies the kit's own
# patches instead.
#
# It is deliberately STRICT - context must match exactly, no fuzz, no offset search. That is the
# right trade here: the recipient's decompile is byte-identical to the one the patch was
# generated against (same ilspycmd version, pinned in .config/dotnet-tools.json, same game
# version, checked before we get here). If context does not match, something is wrong that
# guessing would only hide - most likely a different game version, which is exactly the failure
# a fuzzy match would turn into a corrupted source tree and a baffling compile error.
function Invoke-UnifiedPatch {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$PatchFile,
        [Parameter(Mandatory)] [string]$Root
    )

    $lines = [IO.File]::ReadAllLines($PatchFile)
    $i = 0
    $filesPatched = 0
    $problems = @()

    while ($i -lt $lines.Count) {
        # File header: --- a/<path>  then  +++ b/<path>
        if ($lines[$i] -notmatch '^--- ') { $i++; continue }
        if (($i + 1) -ge $lines.Count -or $lines[$i + 1] -notmatch '^\+\+\+ ') { $i++; continue }

        $rel = ($lines[$i + 1] -replace '^\+\+\+ (b/)?', '').Trim()
        $i += 2

        $path = Join-Path $Root $rel
        if (-not (Test-Path $path)) { $problems += "missing target: $rel"; continue }

        # Read as lines, remembering whether the file ended with a newline so it can be written
        # back the same way.
        $text = [IO.File]::ReadAllText($path)
        $endsWithNewline = $text.EndsWith("`n")
        $content = [Collections.Generic.List[string]]($text -split "`n")
        if ($endsWithNewline -and $content.Count -gt 0) { $content.RemoveAt($content.Count - 1) }

        # Hunks are applied from the BOTTOM UP so that earlier edits do not shift the line
        # numbers of later ones. Collect them first.
        $hunks = @()
        while ($i -lt $lines.Count -and $lines[$i] -match '^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@') {
            $oldStart = [int]$Matches[1]
            $oldLen   = if ($Matches[2]) { [int]$Matches[2] } else { 1 }
            $newLen   = if ($Matches[4]) { [int]$Matches[4] } else { 1 }
            $i++

            # The body is bounded by the DECLARED COUNTS, not by a regex on the line prefix.
            # Matching on the prefix looks correct and is not: the next file's header line
            # "--- a/Whatever.cs" begins with '-', so a prefix-driven loop swallows it as a
            # removed line and every subsequent hunk lands at the wrong offset. That failed as
            # "hunk at line 24 does not match" in a file whose line 24 was fine.
            $body = @()
            $seenOld = 0; $seenNew = 0
            while ($i -lt $lines.Count -and ($seenOld -lt $oldLen -or $seenNew -lt $newLen)) {
                $l = $lines[$i]
                if ($l -match '^\\') { $i++; continue }        # "\ No newline at end of file"
                switch ($l.Substring(0, 1)) {
                    ' ' { $seenOld++; $seenNew++ }
                    '-' { $seenOld++ }
                    '+' { $seenNew++ }
                    default { break }
                }
                $body += $l
                $i++
            }
            $hunks += [pscustomobject]@{ OldStart = $oldStart; OldLen = $oldLen; Body = $body }
        }

        $failed = $false
        for ($h = $hunks.Count - 1; $h -ge 0; $h--) {
            $hunk = $hunks[$h]
            # Unified diff line numbers are 1-based.
            $at = $hunk.OldStart - 1

            # Verify every context and removed line matches before changing anything.
            $probe = $at
            foreach ($b in $hunk.Body) {
                $kind = $b.Substring(0, 1)
                $text2 = if ($b.Length -gt 1) { $b.Substring(1) } else { '' }
                if ($kind -eq '+') { continue }
                if ($probe -ge $content.Count -or $content[$probe] -ne $text2) {
                    $problems += "$rel : hunk at line $($hunk.OldStart) does not match (expected '$text2', found '$(if ($probe -lt $content.Count) { $content[$probe] } else { '<eof>' })')"
                    $failed = $true
                    break
                }
                $probe++
            }
            if ($failed) { break }

            # Apply: replace the old span with the new one.
            $replacement = @()
            foreach ($b in $hunk.Body) {
                $kind = $b.Substring(0, 1)
                $text2 = if ($b.Length -gt 1) { $b.Substring(1) } else { '' }
                if ($kind -eq ' ' -or $kind -eq '+') { $replacement += $text2 }
            }
            $content.RemoveRange($at, $hunk.OldLen)
            if ($replacement.Count) { $content.InsertRange($at, [string[]]$replacement) }
        }

        if ($failed) { continue }

        $outText = ($content -join "`n")
        if ($endsWithNewline) { $outText += "`n" }
        [IO.File]::WriteAllText($path, $outText)
        $filesPatched++
    }

    return [pscustomobject]@{ Files = $filesPatched; Problems = $problems }
}

# ---------------------------------------------------------------- menu animation packer
#
# Builds MainMenuIntro.uwanim from the user's own TauCetiMainMenu.wmv: a motion-JPEG frame
# sequence the game plays without any video decoder. See PORT DEVIATION 17 - MonoGame's
# StbImageSharp already decodes JPEG, so this needs no new dependency and works on every
# backend, whereas the WMV needs MediaFoundation and has no DesktopGL path at all.
#
# Container layout, little-endian, parsed by UWGame.Port.MenuAnimation:
#   0   char[8]  "UWANIM01"
#   8   int32    version (1)
#   12  int32    width
#   16  int32    height
#   20  int32    frame count
#   24  int32    frame delay, milliseconds
#   28  int32[n] length of each JPEG frame
#   ..           the JPEG frames, back to back
function New-MenuAnimation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Ffmpeg,
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string]$Dest,
        [int]$Width = 854,
        [int]$Height = 480,
        [int]$Fps = 12,
        [int]$Quality = 4
    )

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("uwanim-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    try {
        Write-Host "    extracting frames at ${Width}x${Height} @ ${Fps}fps ..."
        & $Ffmpeg -nostdin -v error -y -i $Source -vf "fps=$Fps,scale=${Width}:${Height}" `
                  -pix_fmt yuvj420p -q:v $Quality (Join-Path $tmp '%05d.jpg') 2>&1 | Out-Null

        $frames = Get-ChildItem $tmp -Filter *.jpg | Sort-Object Name
        if ($frames.Count -lt 1) {
            Write-Host '    [warn] ffmpeg produced no frames - the still image will be used' -ForegroundColor Yellow
            return $false
        }

        $blobs = $frames | ForEach-Object { [IO.File]::ReadAllBytes($_.FullName) }
        $fs = [IO.File]::Open($Dest, [IO.FileMode]::Create)
        try {
            $w = New-Object IO.BinaryWriter($fs)
            $w.Write([Text.Encoding]::ASCII.GetBytes('UWANIM01'))
            $w.Write([int]1)
            $w.Write([int]$Width)
            $w.Write([int]$Height)
            $w.Write([int]$blobs.Count)
            $w.Write([int][Math]::Max(1, [Math]::Round(1000.0 / $Fps)))
            foreach ($b in $blobs) { $w.Write([int]$b.Length) }
            foreach ($b in $blobs) { $w.Write($b) }
            $w.Flush()
        } finally { $fs.Dispose() }

        $mb = [Math]::Round((Get-Item $Dest).Length / 1MB, 2)
        Write-Host "    [ok]   $($blobs.Count) frames, $mb MB -> $(Split-Path $Dest -Leaf)" -ForegroundColor Green
        return $true
    } finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
