# The build engine: turns YOUR copy of the game plus this kit's patches into the ported game.
# Called by setup-port.ps1; runnable on its own for debugging.
#
# Nine steps, in order:
#   1. decompile your game's six managed assemblies with ilspycmd
#   2. lay the decompiled trees out as the six source projects
#   3. apply the patch series, and delete the files the port drops
#   4. add the files the port introduces, and the build system
#   5. bootstrap + generate the XML serialization proxies (two-phase, see below)
#   6. build with the selected features
#   7. convert the 19 shaders into port-content\ (your Content\ is never written to)
#   8. optionally build the menu animation from your own video
#   9. publish into the output folder
#
# Everything reads from your installation and writes only inside this kit's folder.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Here,
    [Parameter(Mandatory)] [string]$Game,
    [Parameter(Mandatory)] [string]$Work,
    [Parameter(Mandatory)] [string]$Out,
    [string]$Harmony = 'yes',
    [string]$UnhiddenMod = 'yes',
    [string]$MenuAnimation = 'yes',
    [string]$Ffmpeg,
    [string]$Dotnet
)

$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'lib.ps1')

if (-not $Dotnet) {
    $c = Get-Command dotnet -ErrorAction SilentlyContinue
    $Dotnet = if ($c) { $c.Source } else { "$env:ProgramFiles\dotnet\dotnet.exe" }
}

function Step ($m) { Write-Host ''; Write-Host "==> $m" -ForegroundColor Cyan }
function Ok   ($m) { Write-Host "    [ok]   $m" -ForegroundColor Green }
function Warn ($m) { Write-Host "    [warn] $m" -ForegroundColor Yellow }
function Die  ($m) { Write-Host "    [--]   $m" -ForegroundColor Red; exit 1 }

# Every dotnet invocation goes through this, and the reason is a bug report we could not act on:
# a build failed for a modder and all he had - all ANYONE had - was
#
#     [--]   phase-1 build failed
#
# The compiler had already said exactly what was wrong. The message went nowhere because the
# output was neither kept nor shown, and the run then carried on to the end and printed "Done"
# over a port folder with no game in it. So: keep the output, show the lines that name an error,
# and say where the rest is.
#
# stdout only. Redirecting a native command's stderr with 2>&1 in Windows PowerShell wraps every
# line in an ErrorRecord and sets $? to false even on success, so stderr is left to flow straight
# to the console instead - visible live, just not in the log. MSBuild writes its errors to stdout,
# which is the case that matters here.
#
# Out-Host at the end is not decoration: without it, anything the command emits becomes part of
# THIS function's return value, and a caller testing that return value gets an array instead of a
# boolean. That is how the "Done" above got printed after a failed build.
function Invoke-Dotnet ($What, $LogName, [string[]]$DotnetArgs) {
    $logDir = Join-Path $Work 'logs'
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $log = Join-Path $logDir "$LogName.log"
    & $Dotnet @DotnetArgs | Tee-Object -FilePath $log | Out-Host
    if ($LASTEXITCODE -eq 0) { return }

    $lines = @()
    if (Test-Path $log) {
        $lines = @(Get-Content $log |
            Where-Object { $_ -match '(: error |error CS\d+|error MSB\d+|MSB\d{4}:|Unhandled exception)' } |
            Select-Object -Unique -First 12)
    }
    if ($lines.Count) {
        Write-Host '    what the build said:' -ForegroundColor Red
        foreach ($l in $lines) { Write-Host "      $($l.Trim())" -ForegroundColor Red }
    }
    else {
        Write-Host '    the build printed no line that looks like an error.' -ForegroundColor Red
        Write-Host '    If it also printed nothing above, check that dotnet runs at all:' -ForegroundColor Red
        Write-Host "      & '$Dotnet' --info" -ForegroundColor Red
    }
    Write-Host "    full output: $log" -ForegroundColor Red
    Die "$What failed"
}

# decompiled assembly -> the project directory it becomes.
# RoundLines and InputEventSystem fold into the WindowSystem project, exactly as the port's own
# tree does: the studio shipped them as separate DLLs, and the port merges them so the deployed
# assembly count drops.
# Which kit this is, before anything else, so that a pasted log identifies itself. A build report
# that does not say which version it came from costs a round trip to find out, and the answer has
# already mattered once: a kit whose newfiles/ and patches/ were from different versions.
$kitVersion = Join-Path $Here 'kit-version.txt'
if (Test-Path $kitVersion) {
    Get-Content $kitVersion | Where-Object { $_ -and -not $_.StartsWith('#') } |
        ForEach-Object { Write-Host "    kit $_" -ForegroundColor DarkGray }
}

# Any stamp from a previous run goes now: it means "this run finished", and nothing else.
if ($Work) {
    Remove-Item (Join-Path $Work 'build-succeeded.stamp') -Force -ErrorAction SilentlyContinue
}

$Map = [ordered]@{
    'UnclaimedWorld'         = 'src\UnclaimedWorld'
    'WindowSystem'           = 'src\WindowSystem'
    'RoundLines'             = 'src\WindowSystem'
    'InputEventSystem'       = 'src\WindowSystem'
    'SpriteSheetRuntime'     = 'src\SpriteSheetRuntime'
    'Xclna.Xna.Animationx86' = 'src\AnimationComponentRuntime'
}
$Assemblies = [ordered]@{
    'UnclaimedWorld'         = 'UnclaimedWorld.exe'
    'WindowSystem'           = 'WindowSystem.dll'
    'RoundLines'             = 'RoundLines.dll'
    'InputEventSystem'       = 'InputEventSystem.dll'
    'SpriteSheetRuntime'     = 'SpriteSheetRuntime.dll'
    'Xclna.Xna.Animationx86' = 'Xclna.Xna.Animationx86.dll'
}

$Decomp = Join-Path $Work 'decomp'
# The source tree lays out AT the work root, so <work>\src and <work>\tools end up siblings
# exactly as they are in the repo. An earlier version put the tree in <work>\tree\src while the
# tools went to <work>\tools - two different depths, which broke every relative path between
# them and cost a build cycle to notice.
$Src    = $Work

# ---------------------------------------------------------------- 0. validate the input
#
# The patch series was generated against exact bytes, so the INPUT is verified rather than
# trusted. A FileVersion string is weak evidence: a modded or hand-patched assembly reports the
# same version and then decompiles differently, and the first sign of trouble is a hunk failing
# to apply in a file that has nothing to do with the real problem. A hash says which file.
#
# A mismatch is a warning, not a stop: someone deliberately running the kit against a modified
# assembly is a legitimate thing to want to do, and they will find out soon enough when a patch
# refuses. What is not legitimate is failing silently.
Step 'Verifying your game files'
$hashFile = Join-Path $Here 'assembly-hashes.txt'
if (Test-Path $hashFile) {
    $mismatch = 0; $checked = 0
    Get-Content $hashFile | Where-Object { $_ -match '^[0-9a-f]{64}\s' } | ForEach-Object {
        $parts = $_ -split '\s+', 2
        $want = $parts[0]; $name = $parts[1].Trim()
        $path = Join-Path $Game $name
        if (-not (Test-Path $path)) { Warn "$name not found"; $mismatch++; return }
        $got = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $checked++
        if ($got -ne $want) {
            Warn "$name differs from the copy these patches were built against"
            Write-Host "             expected $($want.Substring(0,16))...  got $($got.Substring(0,16))..."
            $mismatch++
        }
    }
    if ($mismatch -eq 0) { Ok "$checked assembl$(if ($checked -eq 1) {'y'} else {'ies'}) match this kit exactly" }
    else { Warn "$mismatch file(s) differ - patches may not apply" }
} else {
    Warn 'no assembly-hashes.txt in this kit; skipping the hash check'
}

# ---------------------------------------------------------------- 1. decompile
Step 'Decompiling your copy of the game'
New-Item -ItemType Directory -Force -Path $Decomp | Out-Null
foreach ($asm in $Assemblies.Keys) {
    $file = Join-Path $Game $Assemblies[$asm]
    if (-not (Test-Path $file)) { Die "$($Assemblies[$asm]) not found in $Game" }
    $target = Join-Path $Decomp $asm
    if (Test-Path (Join-Path $target 'done.marker')) { Ok "$asm (already decompiled)"; continue }
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Write-Host "    $($Assemblies[$asm]) ..."
    Push-Location $Here
    try {
        # These flags must match build/10-decompile.sh EXACTLY or the patch series will not
        # apply, because the patch paths are the decompiler's output paths:
        #   -p                    project-style tree of .cs files
        #   --nested-directories  UWGame\ClientSide\Client.cs, rather than flattening the
        #                         namespace into a single folder called UWGame.ClientSide.
        #                         Omitting this produced 1523 files with the right count and the
        #                         wrong paths, and every patch reported "missing target".
        #   --use-varnames-from-pdb  no-op here (the game ships no PDBs) but kept so the
        #                         invocation is identical to the one the patches came from.
        & $Dotnet tool run ilspycmd -- -p -o $target $file --nested-directories --use-varnames-from-pdb 2>&1 |
            Where-Object { $_ -match 'error' } | ForEach-Object { Write-Host "      $_" }
    } finally { Pop-Location }
    $n = @(Get-ChildItem $target -Recurse -Filter *.cs).Count
    if ($n -lt 1) { Die "decompiling $asm produced no source" }
    New-Item -ItemType File -Path (Join-Path $target 'done.marker') | Out-Null
    Ok "$asm  ($n files)"
}

# ---------------------------------------------------------------- 2. lay out the projects
#
# Normalised to LF. The patch series was generated from LF text, and a CRLF tree would make
# every hunk fail - the one mistake that cost the most time when this was built, so it is done
# unconditionally rather than hopefully.
Step 'Laying out the source tree'
# Only the laid-out source, NOT $Src itself - $Src is now the work root, which also holds the
# decompile. Removing it wholesale would throw away the slowest step of the whole build.
if (Test-Path (Join-Path $Src 'src')) { Remove-Item (Join-Path $Src 'src') -Recurse -Force }
$seenDest = @{}
foreach ($asm in $Map.Keys) {
    $from = Join-Path $Decomp $asm
    $to   = Join-Path $Src $Map[$asm]
    # Three assemblies fold into src\WindowSystem, and each carries its own
    # Properties\AssemblyInfo.cs. Only the FIRST assembly mapped to a directory contributes
    # Properties\ - otherwise InputEventSystem's assembly attributes land on top of
    # WindowSystem's and the patch fails with
    #   expected '[assembly: AssemblyTitle("WindowSystem")]'
    #   found    '[assembly: AssemblyTitle("InputEventSystem")]'
    # The merged project's identity is WindowSystem's, which is load-critical: XNB files name
    # their reader assemblies, so the assembly name and version must not drift.
    $isPrimary = -not $seenDest.ContainsKey($Map[$asm])
    $seenDest[$Map[$asm]] = $true
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    Get-ChildItem $from -Recurse -Filter *.cs | ForEach-Object {
        $rel  = $_.FullName.Substring($from.Length).TrimStart('\')
        if (-not $isPrimary -and $rel -like 'Properties\*') { return }
        $dest = Join-Path $to $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
        # BYTE-LEVEL, deliberately. Get-Content -Raw decodes text, and on Windows PowerShell 5.1
        # it guesses the codepage when there is no BOM - which mangled the (c) in
        # AssemblyInfo.cs's copyright string into U+FFFD and made that hunk fail with
        #   expected '[assembly: AssemblyCopyright("Copyright c 2016")]'
        #   found    '[assembly: AssemblyCopyright("Copyright <?>c 2016")]'
        # Copying bytes and stripping only CR-before-LF preserves the original encoding, BOM
        # included, so the tree is identical to what the patch series was generated against.
        $b = [IO.File]::ReadAllBytes($_.FullName)
        $o = New-Object 'System.Collections.Generic.List[byte]' ($b.Length)
        for ($k = 0; $k -lt $b.Length; $k++) {
            if ($b[$k] -eq 13 -and ($k + 1) -lt $b.Length -and $b[$k + 1] -eq 10) { continue }
            $o.Add($b[$k])
        }
        [IO.File]::WriteAllBytes($dest, $o.ToArray())
    }
}
Ok "$(@(Get-ChildItem $Src -Recurse -Filter *.cs).Count) files"

# ---------------------------------------------------------------- 3. patch
Step 'Applying the patch series'
foreach ($asm in $Map.Keys) {
    $p = Join-Path $Here "patches\$asm.patch"
    $projDir = Join-Path $Src $Map[$asm]
    if (Test-Path $p) {
        # Invoke-UnifiedPatch rather than GNU patch or git: neither ships with Windows, and the
        # kit is meant to work on a plain machine with nothing but the .NET SDK. See lib.ps1.
        $r = Invoke-UnifiedPatch -PatchFile $p -Root $projDir
        if ($r.Problems.Count) {
            $r.Problems | Select-Object -First 5 | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
            $want = Get-Content (Join-Path $Here 'game-version.txt') | Where-Object { $_ -and -not $_.StartsWith('#') } | Select-Object -First 1
            Die "$asm.patch did not apply. The usual cause is a different game version - this kit's patches are for $want."
        }
        Ok "$asm.patch ($($r.Files) file(s))"
    }
    # Files the port removes outright (stale or superseded by the merge into WindowSystem).
    $del = Join-Path $Here "patches\$asm.deleted"
    if (Test-Path $del) {
        $removed = 0
        Get-Content $del | Where-Object { $_ } | ForEach-Object {
            $f = Join-Path $projDir $_
            if (Test-Path $f) { Remove-Item $f -Force; $removed++ }
        }
        if ($removed) { Ok "${asm}: $removed file(s) removed" }
    }
}

# ---------------------------------------------------------------- 4. our files + build system
Step 'Adding the port''s own files'
$nf = Join-Path $Here 'newfiles'
if (Test-Path $nf) { Ok "$(@(Get-ChildItem $nf -Recurse -Filter *.cs).Count) new source file(s)" }
# newfiles/ and projects/ are laid out as src\<Project>\..., which lands beside tree\ - move
# them in. Kept as separate trees in the kit so the "is this ours?" question stays answerable
# by looking at which folder a file came from.
foreach ($sub in @('newfiles', 'projects')) {
    $root = Join-Path $Here $sub
    if (-not (Test-Path $root)) { continue }
    Get-ChildItem (Join-Path $root 'src') -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
        $rel  = $_.FullName.Substring((Join-Path $root '').Length)
        $dest = Join-Path $Src $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
        Copy-Item $_.FullName $dest -Force
    }
}
foreach ($f in @('Directory.Build.props', 'Directory.Packages.props', 'NuGet.config', 'global.json')) {
    if (Test-Path (Join-Path $Here $f)) { Copy-Item (Join-Path $Here $f) (Join-Path $Work $f) -Force }
}
New-Item -ItemType Directory -Force -Path (Join-Path $Work '.config') | Out-Null
Copy-Item (Join-Path $Here '.config\dotnet-tools.json') (Join-Path $Work '.config\') -Force
if (Test-Path (Join-Path $Here 'tools')) { Copy-Item (Join-Path $Here 'tools') $Work -Recurse -Force }
Ok 'build system in place'
# ---- community files --------------------------------------------------------------------------
#
# newfiles\extra\src\<Project>\... is the same lane for whole files rather than diffs. A mod that
# is a new .cs file has nothing to diff against, and expressing "add this file" as a unified diff
# against nothing works in GNU patch but not in the minimal applier this kit ships - and dropping
# the file in is what everybody tries first anyway.
#
# Copied BEFORE the extra patches would be a mistake and after them is deliberate only in the
# other direction: a patch may target a file this lane provides, so the files land first.
$extraFiles = Join-Path $Here 'newfiles\extra\src'
if (Test-Path $extraFiles) {
    Step 'Adding community files'
    $n = 0
    Get-ChildItem $extraFiles -Recurse -File | ForEach-Object {
        $rel  = $_.FullName.Substring((Join-Path $extraFiles '').Length)
        $dest = Join-Path $Src (Join-Path 'src' $rel)
        New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
        Copy-Item $_.FullName $dest -Force
        $n++
    }
    Ok "$n community file(s)"
}

# ---- community patches ----------------------------------------------------------------------
#
# patches\extra\ is the lane for changes this kit did not ship with. Anyone can drop a .patch in
# and it is applied in filename order - so prefix them (10-, 20-) when order matters. This is what
# makes the kit extensible without a new release: write a diff against the tree and it composes
# with everything else.
#
# IT RUNS HERE, AFTER STEP 4, AND THAT IS THE POINT. It used to run at the end of step 3, with the
# kit's own series - which meant it could only touch files that came out of the DECOMPILE, because
# step 4 then copied newfiles\ over the tree with -Force. A patch against one of the port's own
# files - anything under UWGame\Mods\, say - reported "applied" and was overwritten a step later
# without a word. On a clean work\ the same patch failed the opposite way, "missing target",
# because the file was not there yet. Both are the same bug seen from either side of a stale tree,
# and both are answered by patching after the tree is complete.
#
# The kit's OWN series still runs in step 3: it has to land on the pristine decompile.
#
# Paths inside an extra patch are project-relative, same as the core series, so the file needs to
# say which project it targets. The convention is a folder per project:
#     patches\extra\UnclaimedWorld\10-my-change.patch
$extraRoot = Join-Path $Here 'patches\extra'
if (Test-Path $extraRoot) {
    Step 'Applying community patches'
    foreach ($asm in $Map.Keys) {
        $dir = Join-Path $extraRoot $asm
        if (-not (Test-Path $dir)) { continue }
        $projDir = Join-Path $Src $Map[$asm]
        Get-ChildItem $dir -Filter *.patch | Sort-Object Name | ForEach-Object {
            $r = Invoke-UnifiedPatch -PatchFile $_.FullName -Root $projDir
            if ($r.Problems.Count) {
                $r.Problems | Select-Object -First 3 | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
                # A third-party patch failing must not be mistaken for the kit being broken.
                Die "community patch $($_.Name) did not apply. It was not shipped with this kit - remove it from patches\extra\ to build without it."
            }
            Ok "extra: $($_.Name) ($($r.Files) file(s))"
        }
    }
}


# ---------------------------------------------------------------- 5. XML proxies
#
# Two-phase, and it has to be. CustomXmlSerializer references XmlProxyRegistry directly - so the
# tree cannot compile without it - but the generator produces it by REFLECTING OVER THE BUILT
# GAME. So: seed an empty registry, build, generate the real one, rebuild. The empty registry is
# a bootstrap artefact and is overwritten in the same run.
Step 'Generating the XML serialization proxies'
$proxyDir = Join-Path $Src 'src\UnclaimedWorld\Generated\XmlProxies'
New-Item -ItemType Directory -Force -Path $proxyDir | Out-Null
$registry = Join-Path $proxyDir 'XmlProxyRegistry.g.cs'
if (-not (Test-Path $registry)) {
    @'
// Placeholder registry, seeded by build-port.ps1 to bootstrap a tree with no generated
// proxies. It is overwritten with the real one later in this same run.

using System;
using System.Collections.Generic;

namespace UWGame.Generated.XmlProxies;

internal static class XmlProxyRegistry
{
	internal static readonly Dictionary<Type, Type> ProxyTypes = new();
}
'@ | Set-Content $registry -Encoding UTF8
    Ok 'seeded an empty registry to bootstrap'
}

$gameProj = Join-Path $Src 'src\UnclaimedWorld\UnclaimedWorld.csproj'
Push-Location $Work
try {
    Write-Host '    phase 1: building so the generator has something to reflect over...'
    Invoke-Dotnet 'phase-1 build' 'phase1-build' @('build', $gameProj, '-c', 'Release', '-p:UwPlatform=DX', '-v', 'q', '--nologo')

    # Reconstruct ProxyCodeGenerator.cs from YOUR decompiled CustomXmlSerializer.cs.
    #
    # That file is the game's own CodeDom generator, which PORT DEVIATION 12 moved out of the
    # game. It is the studio's code, so the kit does not carry it - it carries a 342-line diff
    # instead (32 added lines, 18 of context, against 14 KB verbatim). The source must be the
    # UNPATCHED decompile: UnclaimedWorld.patch deletes those 362 lines from
    # CustomXmlSerializer.cs, so the patched tree no longer has them.
    $pcgPatch = Join-Path $Here 'patches\ProxyCodeGenerator.patch'
    $pcgDest  = Join-Path $Work 'tools\XmlProxyGen\ProxyCodeGenerator.cs'
    if (Test-Path $pcgPatch) {
        $cxs = Join-Path $Decomp 'UnclaimedWorld\UWGame\SimSide\CustomXmlSerializer.cs'
        if (-not (Test-Path $cxs)) { Die 'decompiled CustomXmlSerializer.cs not found' }
        $b = [IO.File]::ReadAllBytes($cxs)
        $o = New-Object 'System.Collections.Generic.List[byte]' ($b.Length)
        for ($k = 0; $k -lt $b.Length; $k++) {
            if ($b[$k] -eq 13 -and ($k + 1) -lt $b.Length -and $b[$k + 1] -eq 10) { continue }
            $o.Add($b[$k])
        }
        [IO.File]::WriteAllBytes($pcgDest, $o.ToArray())
        # The patch renames as it transforms, so point it at the destination name.
        $tmpPatch = Join-Path $Work 'pcg.patch'
        (Get-Content $pcgPatch -Raw) -replace '(?m)^\+\+\+ b/.*$', '+++ b/ProxyCodeGenerator.cs' |
            Set-Content $tmpPatch -Encoding UTF8 -NoNewline
        $r = Invoke-UnifiedPatch -PatchFile $tmpPatch -Root (Split-Path $pcgDest)
        Remove-Item $tmpPatch -Force
        if ($r.Problems.Count) {
            $r.Problems | Select-Object -First 3 | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
            Die 'could not reconstruct ProxyCodeGenerator.cs from your decompiled source'
        }
        Ok 'ProxyCodeGenerator.cs reconstructed from your own decompile'
    }

    $bin = Join-Path $Work 'artifacts\bin\UnclaimedWorld\release_dx'
    Invoke-Dotnet 'building XmlProxyGen' 'xmlproxygen' @('build', (Join-Path $Src 'tools\XmlProxyGen\XmlProxyGen.csproj'), '-c', 'Release', '-v', 'q', '--nologo', "-p:UwGameBin=$bin")
    $gen = Get-ChildItem (Join-Path $Work 'artifacts\bin\XmlProxyGen') -Recurse -Filter xmlproxygen.exe |
             Select-Object -First 1
    if (-not $gen) { Die 'xmlproxygen.exe not found' }

    # The generator LOADS the game assembly, so the real runtime assemblies must sit beside it.
    # Steamworks.NET is the one that matters: the copy at the build-output root is a REFERENCE
    # assembly and throws "Cannot load a reference assembly for execution"; the usable one is
    # under runtimes\win-x64. A publish is what puts it there.
    Invoke-Dotnet 'publish for the generator' 'publish-for-generator' @('publish', $gameProj, '-c', 'Release', '-p:UwPlatform=DX', '-v', 'q', '--nologo')
    Copy-Item (Join-Path $bin '*.dll') $gen.DirectoryName -Force -ErrorAction SilentlyContinue
    $rt = Get-ChildItem (Join-Path $Work 'artifacts\publish\UnclaimedWorld\release_dx\runtimes\win-x64') `
            -Recurse -Filter Steamworks.NET.dll -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($rt) { Copy-Item $rt.FullName $gen.DirectoryName -Force }

    Write-Host '    phase 2: generating the real proxies...'
    & $gen.FullName $proxyDir
    if ($LASTEXITCODE -ne 0) { Die 'proxy generation failed' }
    Ok "$(@(Get-ChildItem $proxyDir -Filter *.g.cs).Count) proxy file(s)"
} finally { Pop-Location }

# ---------------------------------------------------------------- 6. build
Step "Building (harmony=$Harmony, unhiddenmod=$UnhiddenMod)"
$h = if ($Harmony -eq 'yes') { 'true' } else { 'false' }
$u = if ($UnhiddenMod -eq 'yes') { 'true' } else { 'false' }
Push-Location $Work
try {
    Invoke-Dotnet 'build' 'build' @('publish', $gameProj, '-c', 'Release', '-p:UwPlatform=DX', "-p:UwHarmony=$h", "-p:UwUnhiddenMod=$u", '-v', 'q', '--nologo')
} finally { Pop-Location }
$pub = Join-Path $Work 'artifacts\publish\UnclaimedWorld\release_dx'
if (-not (Test-Path (Join-Path $pub 'UnclaimedWorld.dll'))) { Die 'no output produced' }
Ok 'built'

# ---------------------------------------------------------------- 9a. stage the output
Step 'Staging the port'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
Get-ChildItem $pub -File | Where-Object { $_.Extension -ne '.pdb' } |
    ForEach-Object { Copy-Item $_.FullName $Out -Force }
foreach ($d in @('runtimes')) {
    $s = Join-Path $pub $d
    if (Test-Path $s) { Copy-Item $s $Out -Recurse -Force }
}
Ok "$(@(Get-ChildItem $Out -File).Count) file(s)"

# ---------------------------------------------------------------- 7. shaders
#
# The 19 shipped effects are MGFX v8 containers and MonoGame 3.8 requires v10. The converted
# copies go in port-content\, which the game prefers over Content\ - so YOUR Content\ is never
# written to. Only the container framing changes; every byte of compiled shader bytecode is
# copied through untouched.
Step 'Converting the shaders into port-content'
$tool = Get-ChildItem (Join-Path $Work 'artifacts\bin\MgfxTranscode') -Recurse -Filter mgfxtranscode.exe `
          -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $tool) {
    Push-Location $Work
    try { Invoke-Dotnet 'building MgfxTranscode' 'mgfxtranscode' @('build', (Join-Path $Src 'tools\MgfxTranscode\MgfxTranscode.csproj'), '-c', 'Release', '-v', 'q', '--nologo') } finally { Pop-Location }
    $tool = Get-ChildItem (Join-Path $Work 'artifacts\bin\MgfxTranscode') -Recurse -Filter mgfxtranscode.exe |
              Select-Object -First 1
}
if (-not $tool) { Die 'mgfxtranscode.exe not found' }

$override = Join-Path $Out 'port-content'
if (Test-Path $override) { Remove-Item $override -Recurse -Force }
New-Item -ItemType Directory -Force -Path $override | Out-Null
$srcContent = Join-Path $Game 'Content'
$copied = 0
Get-ChildItem $srcContent -Recurse -Filter *.xnb | ForEach-Object {
    # An effect .xnb names EffectReader near the top. Match the FULL reader name: "EffectReader"
    # is a substring of "SoundEffectReader", and a loose match would pull in all 220 sounds.
    $head = [IO.File]::ReadAllBytes($_.FullName)[0..([Math]::Min(4095, $_.Length - 1))]
    if ([Text.Encoding]::ASCII.GetString($head) -like '*Microsoft.Xna.Framework.Content.EffectReader*') {
        $rel  = $_.FullName.Substring($srcContent.Length).TrimStart('\')
        $dest = Join-Path $override $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
        Copy-Item $_.FullName $dest -Force
        $copied++
    }
}
& $tool.FullName transcode $override | Select-Object -Last 2
& $tool.FullName validate  $override | Select-Object -Last 1
Ok "$copied effect(s) converted; your Content\ untouched"

# ---------------------------------------------------------------- 8. menu animation
Step 'Menu background animation'
if ($MenuAnimation -ne 'yes') { Warn 'skipped by choice - the still image will be used' }
elseif (-not $Ffmpeg -or -not (Test-Path $Ffmpeg)) { Warn 'no ffmpeg - the still image will be used' }
else {
    $wmv = Join-Path $Game 'Content\MainMenu\TauCetiMainMenu.wmv'
    if (-not (Test-Path $wmv)) { Warn 'TauCetiMainMenu.wmv not found - the still image will be used' }
    else { New-MenuAnimation -Ffmpeg $Ffmpeg -Source $wmv -Dest (Join-Path $Out 'MainMenuIntro.uwanim') }
}

# A stamp, so the caller cannot mistake a failed run for a finished one. Exit codes from a .ps1
# invoked with & are easy to lose - and losing one is what let setup-port.ps1 report "Done" over a
# port with no game exe in it. A file either exists or it does not.
Set-Content -Path (Join-Path $Work 'build-succeeded.stamp') `
            -Value (Get-Date -Format 'o') -Encoding UTF8

Write-Host ''
Ok "port built into $Out"
exit 0
