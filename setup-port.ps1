# Unclaimed World .NET 8 port - self-contained setup and build kit.
#
#   powershell -ExecutionPolicy Bypass -File setup-port.ps1              interactive
#   powershell -ExecutionPolicy Bypass -File setup-port.ps1 -CheckOnly   report and change nothing
#   powershell -ExecutionPolicy Bypass -File setup-port.ps1 -Yes         accept the prompts
#
# WHAT THIS IS, AND WHY IT WORKS THIS WAY.
#
# This kit contains NO game code and NO game assets. It contains a patch series, a handful of
# new source files, build tooling, and this script. Everything of the studio's comes off YOUR
# copy of the game, decompiled on YOUR machine. That is a deliberate constraint, not an
# accident of packaging: the port is a derivative work of a game whose developer we have not
# been able to reach for permission, so nothing of theirs is distributed here.
#
# The consequence is that this script has real work to do - fetch a decompiler, decompile,
# patch, build - which is why it asks before it downloads or installs anything, and why every
# step reports what it is about to do.
#
# It installs LOCALLY wherever it can. Nothing goes into Program Files, the registry, the PATH,
# or the global dotnet tool store, except where noted and confirmed. Deleting this folder
# removes everything except the .NET SDK, which is the one thing that cannot be made local.
[CmdletBinding()]
param(
    # Report what would happen and change nothing.
    [switch]$CheckOnly,
    # Accept the download/install and build prompts. Still refuses anything destructive.
    [switch]$Yes,
    # Where the original game is. Found automatically if omitted.
    [string]$Game,
    # Feature selection. Both default on; see the SELECTABLE FEATURES block in the csproj.
    [ValidateSet('yes', 'no')] [string]$Harmony = 'yes',
    [ValidateSet('yes', 'no')] [string]$UnhiddenMod = 'yes',
    [ValidateSet('yes', 'no')] [string]$MenuAnimation = 'yes',
    # How the game's assets get into the build: auto tries a junction then offers a copy.
    [ValidateSet('auto', 'link', 'copy', 'none')] [string]$Assets = 'auto'
)

$ErrorActionPreference = 'Stop'
$Here    = (Resolve-Path (Split-Path -Parent $MyInvocation.MyCommand.Path)).Path
$Work    = Join-Path $Here 'work'          # decompile + patched source
$OutDir  = Join-Path $Here 'port'          # the built game
$Prereq  = Join-Path $Here 'prereqs'       # locally installed tools

$script:Missing  = @()   # things to fetch, each @{ Name; Size; How; Local }
$script:Blocking = @()   # things this script cannot fix

function Say    ($m) { Write-Host $m }
function Step   ($m) { Write-Host ''; Write-Host "==> $m" -ForegroundColor Cyan }
function Ok     ($m) { Write-Host "    [ok]   $m" -ForegroundColor Green }
function Warn   ($m) { Write-Host "    [warn] $m" -ForegroundColor Yellow }
function Bad    ($m) { Write-Host "    [--]   $m" -ForegroundColor Red }

function Confirm-Step([string]$Question) {
    if ($Yes) { Say "    $Question -> yes (-Yes)"; return $true }
    if ($CheckOnly) { return $false }
    $a = Read-Host "    $Question [y/N]"
    return $a -match '^(y|yes)$'
}

# ---------------------------------------------------------------- 1. prerequisites
#
# Split into three kinds, because the right response differs:
#   * BLOCKING  - cannot be fixed from here (the .NET SDK). Report and stop.
#   * FETCHABLE - can be installed into prereqs\ without touching the system.
#   * OPTIONAL  - only needed for a feature the user may have switched off.

# `dotnet` is not always on PATH even when the SDK is installed - a shell launched from an
# environment that sets its own PATH will not have it, and this script was reporting "SDK not
# found" on a machine that had been building all day. So look in the default install location
# too, and use the resolved path everywhere rather than relying on PATH resolution.
function Resolve-Dotnet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($p in @("$env:ProgramFiles\dotnet\dotnet.exe",
                     "${env:ProgramFiles(x86)}\dotnet\dotnet.exe",
                     "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe")) {
        if (Test-Path $p) { return $p }
    }
    return $null
}

function Test-Prerequisites {
    Step 'Checking prerequisites'

    # --- .NET SDK 8. Not installable locally in any honest sense: it is a machine-wide runtime
    # --- plus MSBuild, and a private copy would be several hundred MB and still need a host.
    $script:Dotnet = Resolve-Dotnet
    $sdk = $null
    if ($script:Dotnet) {
        try {
            $sdks = & $script:Dotnet --list-sdks 2>$null
            $sdk = $sdks | Where-Object { $_ -match '^8\.' } | Select-Object -Last 1
        } catch { }
    }
    if ($sdk) {
        Ok ".NET SDK 8 ($(($sdk -split ' ')[0]))  [$script:Dotnet]"
    } else {
        Bad '.NET SDK 8 not found'
        $script:Blocking += [pscustomobject]@{
            Name = '.NET SDK 8'
            Why  = 'Needed to decompile and build. It cannot be installed into this folder.'
            Fix  = 'https://dotnet.microsoft.com/download/dotnet/8.0  (SDK, x64)'
        }
    }

    # --- ilspycmd, the decompiler. A dotnet LOCAL tool: restored from the manifest in this
    # --- folder, invoked as `dotnet tool run ilspycmd`, and not added to the PATH.
    $manifest = Join-Path $Here '.config\dotnet-tools.json'
    if (-not (Test-Path $manifest)) {
        Bad 'dotnet-tools.json missing - this kit is incomplete'
        $script:Blocking += [pscustomobject]@{
            Name = 'kit integrity'; Why = '.config\dotnet-tools.json is missing.'; Fix = 'Re-download the kit.'
        }
    } else {
        $restored = $false
        try {
            Push-Location $Here
            $null = & $script:Dotnet tool run ilspycmd -- --version 2>$null
            $restored = ($LASTEXITCODE -eq 0)
        } catch { } finally { Pop-Location }

        if ($restored) {
            Ok 'ilspycmd (local dotnet tool, already restored)'
        } else {
            Warn 'ilspycmd not restored yet'
            $script:Missing += [pscustomobject]@{
                Name  = 'ilspycmd + MonoGame content tools (local dotnet tools)'
                Size  = 40MB
                How   = 'dotnet tool restore'
                Local = $true
            }
        }
    }

    # --- ffmpeg, only for the menu animation. Portable zip, unpacked into prereqs\.
    if ($MenuAnimation -eq 'yes') {
        $ff = Get-FfmpegPath
        if ($ff) { Ok "ffmpeg ($ff)" }
        else {
            Warn 'ffmpeg not found (needed only for the menu background animation)'
            $script:Missing += [pscustomobject]@{
                Name  = 'ffmpeg (portable, into prereqs\ffmpeg)'
                Size  = 90MB
                How   = 'download + unzip, no installer'
                Local = $true
            }
        }
    }

    # --- the game itself.
    $g = Resolve-GameDir
    if ($g) {
        Ok "game found: $g"
        $script:GameDir = $g
        Test-GameVersion $g
    } else {
        Bad 'game installation not found'
        $script:Blocking += [pscustomobject]@{
            Name = 'Unclaimed World installation'
            Why  = 'The kit needs your own copy: it contains no game code or assets.'
            Fix  = 'Install the game, or pass -Game "C:\Path\To\Unclaimed World"'
        }
    }
}

function Get-FfmpegPath {
    $local = Join-Path $Prereq 'ffmpeg\bin\ffmpeg.exe'
    if (Test-Path $local) { return $local }
    $cmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    # winget's package layout, which is where most people already have it.
    $wg = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter ffmpeg.exe `
            -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($wg) { return $wg.FullName }
    return $null
}

function Resolve-GameDir {
    if ($Game) { if (Test-GameDir $Game) { return (Resolve-Path $Game).Path } else { return $null } }
    $guesses = @(
        "${env:ProgramFiles(x86)}\Steam\steamapps\common\Unclaimed World",
        "$env:ProgramFiles\Steam\steamapps\common\Unclaimed World"
    )
    $vdf = "${env:ProgramFiles(x86)}\Steam\steamapps\libraryfolders.vdf"
    if (Test-Path $vdf) {
        foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s*"([^"]+)"')) {
            $guesses += (Join-Path ($m.Groups[1].Value -replace '\\\\', '\') 'steamapps\common\Unclaimed World')
        }
    }
    foreach ($p in $guesses) { if (Test-GameDir $p) { return (Resolve-Path $p).Path } }
    return $null
}

function Test-GameDir([string]$p) {
    if (-not $p) { return $false }
    return (Test-Path (Join-Path $p 'Content')) -and (Test-Path (Join-Path $p 'data')) -and
           (Test-Path (Join-Path $p 'UnclaimedWorld.exe'))
}

# The port is built from ONE game version's decompiled code. A mismatch does not fail the build
# - it fails much later, at content load, with a bare cast exception that names no version. So
# it is checked here, before anything is downloaded.
function Test-GameVersion([string]$g) {
    $expectedFile = Join-Path $Here 'game-version.txt'
    if (-not (Test-Path $expectedFile)) { return }
    $expected = (Get-Content $expectedFile | Where-Object { $_ -and -not $_.StartsWith('#') } |
                 Select-Object -First 1).Trim()
    $actual = (Get-Item (Join-Path $g 'UnclaimedWorld.exe')).VersionInfo.FileVersion
    if ($actual -eq $expected) { Ok "game version $actual (matches this kit)" }
    else {
        Bad "game version $actual, but this kit's patches are for $expected"
        $script:Blocking += [pscustomobject]@{
            Name = 'game version'
            Why  = "The patches apply to $expected. Yours is $actual, so they will not apply cleanly."
            Fix  = 'Switch version in Steam (Properties > Betas), or get the kit for your version.'
        }
    }
}

# ---------------------------------------------------------------- 2/3/4. consent, then fetch

function Invoke-Prerequisites {
    if ($script:Missing.Count -eq 0) { Ok 'nothing to download'; return $true }

    Step 'These components are missing'
    $total = 0
    foreach ($m in $script:Missing) {
        $total += $m.Size
        $where = if ($m.Local) { 'into this folder only' } else { 'SYSTEM-WIDE' }
        Say ("    - {0}`n        {1:N0} MB, {2}, {3}" -f $m.Name, ($m.Size / 1MB), $m.How, $where)
    }
    Say ''
    Say ("    {0} item(s), about {1:N0} MB to download." -f $script:Missing.Count, ($total / 1MB))
    Say '    Everything above installs inside this folder. Deleting the folder removes it.'

    if ($CheckOnly) { Warn 'check-only: nothing was downloaded'; return $false }
    if (-not (Confirm-Step 'Download and install these now?')) {
        Warn 'declined - cannot continue without them'
        return $false
    }

    New-Item -ItemType Directory -Force -Path $Prereq | Out-Null
    foreach ($m in $script:Missing) {
        switch -Wildcard ($m.Name) {
            'ilspycmd*' { Step 'Restoring local dotnet tools'
                          Push-Location $Here
                          try { & $script:Dotnet tool restore } finally { Pop-Location }
                          if ($LASTEXITCODE -ne 0) { Bad 'dotnet tool restore failed'; return $false }
                          Ok 'tools restored' }
            'ffmpeg*'   { if (-not (Install-Ffmpeg)) { return $false } }
        }
    }
    return $true
}

function Install-Ffmpeg {
    Step 'Fetching ffmpeg (portable)'
    $url = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
    $zip = Join-Path $Prereq 'ffmpeg.zip'
    $dst = Join-Path $Prereq 'ffmpeg'
    try {
        Say "    $url"
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
        if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
        Expand-Archive -Path $zip -DestinationPath "$dst.tmp" -Force
        # The zip has a single versioned top-level folder; flatten it so the path is stable.
        $inner = Get-ChildItem "$dst.tmp" -Directory | Select-Object -First 1
        Move-Item $inner.FullName $dst
        Remove-Item "$dst.tmp" -Recurse -Force
        Remove-Item $zip -Force
        if (-not (Test-Path (Join-Path $dst 'bin\ffmpeg.exe'))) { throw 'ffmpeg.exe not where expected' }
        Ok "ffmpeg -> $dst"
        return $true
    } catch {
        Bad "ffmpeg download failed: $($_.Exception.Message)"
        Warn 'continuing without it - the menu will use the still background image'
        $script:MenuAnimationPossible = $false
        return $true    # not fatal
    }
}

# ---------------------------------------------------------------- 7/8. assets
#
# Content\ can be a junction: the port never writes to it (the converted shaders live in
# port-content\). data\ must be a real copy, because --export-data writes into data\BaseData\
# and a junction would put that inside the player's real game folder.
#
# Junctions are used rather than symlinks because /J needs no administrator rights, whereas a
# directory symlink does unless Developer Mode is on. Support is established by CREATING one and
# checking it resolves - not by reading the filesystem name, since the answer depends on the
# volume (exFAT and FAT32 cannot hold reparse points at all).

function Test-JunctionSupport([string]$dir) {
    $probe  = Join-Path $dir '.uw-link-probe'
    $target = Join-Path $dir '.uw-link-target'
    try {
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        & cmd /c mklink /J "$probe" "$target" 2>&1 | Out-Null
        $ok = (Test-Path $probe) -and
              ((Get-Item $probe -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)
        return [bool]$ok
    } catch { return $false } finally {
        if (Test-Path $probe)  { & cmd /c rmdir "$probe" 2>&1 | Out-Null }
        if (Test-Path $target) { Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Get-FilesystemName([string]$dir) {
    try { return (Get-Volume -FilePath $dir -ErrorAction Stop).FileSystemType } catch { }
    try {
        $root = [IO.Path]::GetPathRoot((Resolve-Path $dir))
        return (Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$($root.TrimEnd('\'))'").FileSystem
    } catch { return 'unknown' }
}

function Install-Assets {
    Step 'Game assets'
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

    $fs = Get-FilesystemName $OutDir
    Say "    target filesystem: $fs"

    $mode = $Assets
    if ($mode -eq 'auto') {
        if (Test-JunctionSupport $OutDir) {
            Ok 'this filesystem supports junctions - Content can be linked instead of copied'
            $mode = 'link'
        } else {
            Warn "$fs does not support junctions (exFAT and FAT32 cannot)"
            Say  '    Content must therefore be copied: 317 MB, plus 102 MB for data.'
            $mode = if (Confirm-Step 'Copy the game assets here (about 420 MB)?') { 'copy' } else { 'none' }
        }
    }

    $srcContent = Join-Path $script:GameDir 'Content'
    $dstContent = Join-Path $OutDir 'Content'
    $srcData    = Join-Path $script:GameDir 'data'
    $dstData    = Join-Path $OutDir 'data'

    if ($mode -eq 'none') {
        Warn 'skipping assets'
        Say  "    Before running the port, put these in $OutDir yourself:"
        Say  "        Content\   (copy or link of $srcContent)"
        Say  "        data\      (COPY of $srcData - it is written to, do not link it)"
        return
    }

    if (Test-Path $dstContent) { & cmd /c rmdir "$dstContent" 2>&1 | Out-Null }
    if (Test-Path $dstContent) { Remove-Item $dstContent -Recurse -Force }

    if ($mode -eq 'link') {
        & cmd /c mklink /J "$dstContent" "$srcContent" | Out-Null
        if (-not (Test-Path $dstContent)) { throw "could not link $dstContent" }
        Ok 'Content\  -> junction to your game (0 bytes copied, never written to)'
        Say '    To remove it later use:  cmd /c rmdir "' + $dstContent + '"'
        Say '    rmdir deletes the junction only. Tools that FOLLOW junctions could delete your'
        Say '    real game content, so do not point a recursive delete at this folder.'
    } else {
        Say '    copying Content\ (317 MB)...'
        Copy-Item $srcContent $dstContent -Recurse
        Ok 'Content\  copied'
    }

    if (Test-Path $dstData) { Remove-Item $dstData -Recurse -Force }
    Say '    copying data\ (102 MB - always copied, --export-data writes into it)...'
    Copy-Item $srcData $dstData -Recurse
    Ok 'data\  copied'

    $appid = Join-Path $script:GameDir 'steam_appid.txt'
    if (Test-Path $appid) { Copy-Item $appid (Join-Path $OutDir 'steam_appid.txt') -Force; Ok 'steam_appid.txt' }
}

# ---------------------------------------------------------------- 5/6. build

function Invoke-Build {
    Step 'Ready to build'
    Say  "    source of game code : $script:GameDir  (decompiled here, on your machine)"
    Say  "    working directory   : $Work"
    Say  "    output              : $OutDir"
    Say  "    features            : harmony=$Harmony  unhiddenmod=$UnhiddenMod  menuAnimation=$MenuAnimation"
    Say  ''
    Say  '    This decompiles your copy of the game, applies the patch series, and builds.'
    Say  '    Your game folder is only READ. Nothing in it is modified.'

    if ($CheckOnly) { Warn 'check-only: nothing was built'; return $false }
    if (-not (Confirm-Step 'Proceed with the build?')) { Warn 'declined'; return $false }

    $decompile = Join-Path $Here 'scripts\build-port.ps1'
    if (-not (Test-Path $decompile)) {
        Bad "missing scripts\build-port.ps1 - kit is incomplete"
        return $false
    }

    $stamp = Join-Path $Work 'build-succeeded.stamp'
    Remove-Item $stamp -Force -ErrorAction SilentlyContinue

    # Out-Host, not a bare call: anything the child script leaves in the pipeline would otherwise
    # become part of THIS function's return value, and `if (-not (Invoke-Build))` on an array is
    # false however the build went. That is how a failed build was followed by "Done".
    & $decompile -Here $Here -Game $script:GameDir -Work $Work -Out $OutDir `
        -Harmony $Harmony -UnhiddenMod $UnhiddenMod `
        -MenuAnimation $(if ($script:MenuAnimationPossible -eq $false) { 'no' } else { $MenuAnimation }) `
        -Ffmpeg (Get-FfmpegPath) | Out-Host
    $code = $LASTEXITCODE

    # Two independent answers, because an exit code from a .ps1 called with & is easy to lose and
    # the consequence of losing it is a "Done" over an empty port folder.
    if (-not (Test-Path $stamp)) {
        Bad 'the build did not finish - see the messages above'
        Say "        the full compiler output is under $(Join-Path $Work 'logs')"
        return $false
    }
    return ($code -eq 0)
}

# ---------------------------------------------------------------- main

Say ''
Say '  Unclaimed World - .NET 8 port setup'
Say '  ==================================='
Say '  This kit contains no game code and no game assets. Both come from your own'
Say '  installed copy, decompiled and built on this machine.'

Test-Prerequisites

if ($script:Blocking.Count) {
    Step 'Cannot continue'
    foreach ($b in $script:Blocking) {
        Bad $b.Name
        Say "        $($b.Why)"
        Say "        -> $($b.Fix)"
    }
    exit 2
}

if (-not (Invoke-Prerequisites)) { exit ($(if ($CheckOnly) { 0 } else { 1 })) }

if ($CheckOnly) {
    Step 'Check complete'
    Ok 'all prerequisites present or fetchable; re-run without -CheckOnly to build'
    exit 0
}

if (-not (Invoke-Build)) { exit 1 }

Install-Assets

Step 'Done'
Ok "run the port with: $(Join-Path $OutDir 'UnclaimedWorld.exe')"
Say '    Your game folder was not modified.'
