// uwkit - the Unclaimed World .NET 8 port build orchestrator.
//
// A single static binary that replaces the kit's three PowerShell scripts. That is the point of
// it: PowerShell is disabled by policy on plenty of machines, cmd.exe cannot read a file version
// (wmic is gone from Windows 11), and neither GNU patch nor git ships with Windows. This needs
// none of them. The only external tool is `dotnet`, which is unavoidable.
//
// Deliberately stdlib-only - no golang.org/x/sys, no module downloads - so the kit builds
// offline and the binary stays around 2 MB.
//
// What it does, which is exactly the four steps the kit is built around:
//  1. find the game, verify its version AND the SHA-256 of all six assemblies
//  2. decompile them with ilspycmd
//  3. apply the patch series (plus anything in patches/extra)
//  4. build, convert the shaders, and set up the assets
package main

import (
	"archive/zip"
	"bufio"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"syscall"
	"unsafe"
)

// ---------------------------------------------------------------- layout

// decompiled assembly -> the project directory it becomes, in order. RoundLines and
// InputEventSystem fold into the WindowSystem project, matching the port's own tree.
type mapping struct {
	Asm, File, Proj string
}

var mappings = []mapping{
	{"UnclaimedWorld", "UnclaimedWorld.exe", `src\UnclaimedWorld`},
	{"WindowSystem", "WindowSystem.dll", `src\WindowSystem`},
	{"RoundLines", "RoundLines.dll", `src\WindowSystem`},
	{"InputEventSystem", "InputEventSystem.dll", `src\WindowSystem`},
	{"SpriteSheetRuntime", "SpriteSheetRuntime.dll", `src\SpriteSheetRuntime`},
	{"Xclna.Xna.Animationx86", "Xclna.Xna.Animationx86.dll", `src\AnimationComponentRuntime`},
}

type opts struct {
	here, game, work, out string
	dotnet, ffmpeg        string
	harmony, mod, anim    bool
	assets                string // auto|link|copy|none
	yes, checkOnly        bool
}

func main() {
	o := parseFlags()
	fmt.Println()
	fmt.Println("  Unclaimed World - .NET 8 port build kit")
	fmt.Println("  =======================================")
	fmt.Println("  This kit contains no game code and no game assets. Both come from your")
	fmt.Println("  own installed copy, decompiled and built on this machine.")

	err := run(o)
	if err != nil {
		fmt.Printf("\n  BUILD DID NOT COMPLETE\n\n  %v\n", err)
		fmt.Println("\n  What to do:")
		fmt.Println("    * the log above ends at the first real error - that is the one to read")
		fmt.Println("    * run  uwkit -check  to re-test the prerequisites without building")
		fmt.Println("    * a full build log is in  work\\build.log")
		fmt.Println("    * nothing in your game folder was modified")
	}
	// Pause before exiting, ALWAYS, unless the run was non-interactive. Double-clicking the exe
	// in Explorer gives it its own console window, which Windows destroys the moment the process
	// returns - so every message, including the error explaining what to fix, vanished before it
	// could be read. This was reported as "throws warnings then auto-closes with no
	// instructions", which is exactly what it looked like from the outside.
	if !o.yes {
		fmt.Print("\n  Press Enter to close... ")
		bufio.NewReader(os.Stdin).ReadString('\n')
	}
	if err != nil {
		os.Exit(1)
	}
}

func run(o *opts) error {
	printKitVersion(o)
	if err := checkPrereqs(o); err != nil {
		return err
	}
	if o.checkOnly {
		step("Check complete")
		ok("re-run without -check to build")
		return nil
	}
	if !confirm(o, "Decompile your game and build the port?") {
		return fmt.Errorf("declined")
	}
	for _, f := range []func(*opts) error{decompile, layout, applyPatches, addOurFiles, addCommunityFiles, applyCommunityPatches, genProxies, build, stage, shaders, animation, assets, steamNative} {
		if err := f(o); err != nil {
			return err
		}
	}
	step("Done")
	ok("run the port with: " + filepath.Join(o.out, "UnclaimedWorld.exe"))
	fmt.Println("       Your game folder was not modified.")
	return nil
}

// Which kit this is, before anything else, so that a pasted build log identifies itself. A report
// that does not say which version it came from costs a round trip to find out, and the answer has
// already mattered once: a kit whose newfiles/ and patches/ had come from different versions,
// which a log saying "8 new source file(s)" does not reveal unless you know 13 is the number.
func printKitVersion(o *opts) {
	b, err := os.ReadFile(filepath.Join(o.here, "kit-version.txt"))
	if err != nil {
		return
	}
	for _, line := range strings.Split(strings.TrimSpace(string(b)), "\n") {
		line = strings.TrimSpace(line)
		if line != "" && !strings.HasPrefix(line, "#") {
			fmt.Println("       kit " + line)
		}
	}
}

// ---------------------------------------------------------------- 1. prerequisites

func checkPrereqs(o *opts) error {
	step("Checking prerequisites")

	// The .NET SDK is the one thing that cannot be made local: it is a machine-wide runtime plus
	// MSBuild. Resolved from the standard install locations as well as PATH, because a shell
	// that sets its own PATH will not have it and "SDK not found" on a machine that has it is a
	// bad first impression.
	o.dotnet = which("dotnet",
		filepath.Join(os.Getenv("ProgramFiles"), `dotnet\dotnet.exe`),
		filepath.Join(os.Getenv("ProgramFiles(x86)"), `dotnet\dotnet.exe`),
		filepath.Join(os.Getenv("LOCALAPPDATA"), `Microsoft\dotnet\dotnet.exe`))
	if o.dotnet == "" {
		return fmt.Errorf(".NET SDK 8 not found.\n       Install it: https://dotnet.microsoft.com/download/dotnet/8.0")
	}
	out, _ := run2(o.dotnet, o.here, "--list-sdks")
	if !strings.Contains(out, "\n8.") && !strings.HasPrefix(out, "8.") {
		return fmt.Errorf(".NET SDK 8 is required; found:\n%s", indent(out))
	}
	ok(".NET SDK 8  [" + o.dotnet + "]")

	// The game.
	if o.game == "" {
		o.game = findGame()
	}
	if o.game == "" || !isGameDir(o.game) {
		return fmt.Errorf("game installation not found.\n       Pass it: uwkit -game \"C:\\Path\\To\\Unclaimed World\"")
	}
	ok("game: " + o.game)

	if err := verifyGame(o); err != nil {
		return err
	}

	// ilspycmd, a LOCAL dotnet tool - restored into this folder, not the global store.
	if _, err := run2(o.dotnet, o.here, "tool", "run", "ilspycmd", "--", "--version"); err != nil {
		if o.checkOnly {
			warn("local dotnet tools not restored yet (about 40 MB)")
		} else {
			if !confirm(o, "Restore the ILSpy decompiler as a local tool (~40 MB)?") {
				return fmt.Errorf("cannot continue without the decompiler")
			}
			step("Restoring local dotnet tools")
			if err := stream(o.dotnet, o.here, "tool", "restore"); err != nil {
				return fmt.Errorf("dotnet tool restore failed: %w", err)
			}
			ok("tools restored")
		}
	} else {
		ok("ilspycmd (local dotnet tool)")
	}

	// ffmpeg, only for the animated menu background.
	if o.anim {
		o.ffmpeg = findFfmpeg(o.here)
		if o.ffmpeg == "" {
			if o.checkOnly || !confirm(o, "Download a portable ffmpeg for the menu animation (~90 MB)?") {
				warn("no ffmpeg - the menu will use the still background image")
				o.anim = false
			} else if err := getFfmpeg(o); err != nil {
				warn("ffmpeg download failed: " + err.Error() + " - using the still image")
				o.anim = false
			}
		} else {
			ok("ffmpeg: " + o.ffmpeg)
		}
	}
	return nil
}

// verifyGame checks the version string AND the SHA-256 of every assembly the patches touch.
//
// The hash is the real check. A version string is weak evidence: a modded or hand-patched
// assembly reports the same FileVersion and then decompiles differently, and the first symptom
// is a hunk failing to apply in a file that has nothing to do with the cause. A hash names the
// file. It warns rather than stops, because deliberately building against a modified assembly is
// a legitimate thing to want to do.
func verifyGame(o *opts) error {
	if want, err := readFirstLine(filepath.Join(o.here, "game-version.txt")); err == nil {
		got, err := fileVersion(filepath.Join(o.game, "UnclaimedWorld.exe"))
		switch {
		case err != nil:
			warn("could not read the game's version: " + err.Error())
		case got != want:
			warn(fmt.Sprintf("game is %s, this kit's patches are for %s", got, want))
			fmt.Println("       The patches will probably not apply. Switch version in Steam")
			fmt.Println("       (Properties > Betas), or get the kit for your version.")
		default:
			ok("game version " + got + " (matches this kit)")
		}
	}

	f, err := os.Open(filepath.Join(o.here, "assembly-hashes.txt"))
	if err != nil {
		warn("no assembly-hashes.txt; skipping the hash check")
		return nil
	}
	defer f.Close()
	bad, n := 0, 0
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		line := strings.TrimSpace(sc.Text())
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		parts := strings.Fields(line)
		if len(parts) != 2 {
			continue
		}
		got, err := sha256File(filepath.Join(o.game, parts[1]))
		n++
		if err != nil {
			warn(parts[1] + ": " + err.Error())
			bad++
		} else if got != parts[0] {
			warn(parts[1] + " differs from the copy these patches were built against")
			bad++
		}
	}
	if bad == 0 {
		ok(fmt.Sprintf("%d assemblies match this kit exactly", n))
	} else {
		warn(fmt.Sprintf("%d of %d file(s) differ - patches may not apply", bad, n))
	}
	return nil
}

// ---------------------------------------------------------------- 2. decompile

func decompile(o *opts) error {
	step("Decompiling your copy of the game")
	dec := filepath.Join(o.work, "decomp")
	os.MkdirAll(dec, 0o755)
	for _, m := range mappings {
		src := filepath.Join(o.game, m.File)
		if _, err := os.Stat(src); err != nil {
			return fmt.Errorf("%s not found in %s", m.File, o.game)
		}
		target := filepath.Join(dec, m.Asm)
		if _, err := os.Stat(filepath.Join(target, "done.marker")); err == nil {
			ok(m.Asm + " (already decompiled)")
			continue
		}
		os.RemoveAll(target)
		os.MkdirAll(target, 0o755)
		fmt.Printf("       %s ...\n", m.File)
		// These flags must match the ones the patch series was generated with, or the paths in
		// the patches will not exist. --nested-directories in particular: without it ILSpy
		// flattens the namespace into one folder (UWGame.ClientSide instead of
		// UWGame\ClientSide) and every patch reports "missing target" while the file COUNT is
		// still correct, which is a thoroughly misleading failure.
		if err := stream(o.dotnet, o.here, "tool", "run", "ilspycmd", "--",
			"-p", "-o", target, src, "--nested-directories", "--use-varnames-from-pdb"); err != nil {
			return fmt.Errorf("decompiling %s: %w", m.Asm, err)
		}
		n := countFiles(target, ".cs")
		if n == 0 {
			return fmt.Errorf("decompiling %s produced no source", m.Asm)
		}
		os.WriteFile(filepath.Join(target, "done.marker"), nil, 0o644)
		ok(fmt.Sprintf("%s  (%d files)", m.Asm, n))
	}
	return nil
}

// ---------------------------------------------------------------- 3. lay out + patch

func layout(o *opts) error {
	step("Laying out the source tree")
	os.RemoveAll(filepath.Join(o.work, "src"))
	seen := map[string]bool{}
	total := 0
	for _, m := range mappings {
		from := filepath.Join(o.work, "decomp", m.Asm)
		to := filepath.Join(o.work, m.Proj)
		// Three assemblies fold into src\WindowSystem and each carries its own
		// Properties\AssemblyInfo.cs. Only the first assembly mapped to a directory contributes
		// Properties\ - otherwise InputEventSystem's assembly attributes land on top of
		// WindowSystem's, and the merged project's identity is load-critical because XNB files
		// name their reader assemblies.
		primary := !seen[m.Proj]
		seen[m.Proj] = true
		err := filepath.Walk(from, func(p string, fi os.FileInfo, err error) error {
			if err != nil || fi.IsDir() || !strings.HasSuffix(p, ".cs") {
				return err
			}
			rel := strings.TrimPrefix(strings.TrimPrefix(p, from), `\`)
			if !primary && strings.HasPrefix(rel, `Properties\`) {
				return nil
			}
			dest := filepath.Join(to, rel)
			os.MkdirAll(filepath.Dir(dest), 0o755)
			// Byte-level, so the original encoding and BOM survive exactly. Decoding as text
			// and re-encoding mangles non-ASCII when there is no BOM - it turned the (c) in
			// AssemblyInfo's copyright string into U+FFFD and made that hunk fail.
			b, err := os.ReadFile(p)
			if err != nil {
				return err
			}
			total++
			return os.WriteFile(dest, stripCR(b), 0o644)
		})
		if err != nil {
			return err
		}
	}
	ok(fmt.Sprintf("%d files", total))
	return nil
}

func applyPatches(o *opts) error {
	step("Applying the patch series")
	for _, m := range mappings {
		proj := filepath.Join(o.work, m.Proj)
		p := filepath.Join(o.here, "patches", m.Asm+".patch")
		if _, err := os.Stat(p); err == nil {
			n, probs := applyUnified(p, proj)
			if len(probs) > 0 {
				for _, s := range probs[:min(3, len(probs))] {
					fmt.Println("       " + s)
				}
				want, _ := readFirstLine(filepath.Join(o.here, "game-version.txt"))
				return fmt.Errorf("%s.patch did not apply. The usual cause is a different game version - this kit's patches are for %s", m.Asm, want)
			}
			ok(fmt.Sprintf("%s.patch (%d file(s))", m.Asm, n))
		}
		// Files the port drops outright.
		if f, err := os.Open(filepath.Join(o.here, "patches", m.Asm+".deleted")); err == nil {
			removed := 0
			sc := bufio.NewScanner(f)
			for sc.Scan() {
				if rel := strings.TrimSpace(sc.Text()); rel != "" {
					if os.Remove(filepath.Join(proj, rel)) == nil {
						removed++
					}
				}
			}
			f.Close()
			if removed > 0 {
				ok(fmt.Sprintf("%s: %d file(s) removed", m.Asm, removed))
			}
		}
	}
	return nil
}

// Whole files contributed by someone other than this kit: newfiles\extra\src\<Project>\...
//
// A mod that is a new .cs file has nothing to diff against, and expressing "add this file" as a
// unified diff against nothing works in GNU patch but not in the minimal applier here.
func addCommunityFiles(o *opts) error {
	root := filepath.Join(o.here, "newfiles", "extra", "src")
	if _, err := os.Stat(root); err != nil {
		return nil
	}
	step("Adding community files")
	n := 0
	filepath.Walk(root, func(p string, fi os.FileInfo, err error) error {
		if err != nil || fi.IsDir() {
			return nil
		}
		rel := strings.TrimPrefix(strings.TrimPrefix(p, root), `\`)
		dest := filepath.Join(o.work, "src", rel)
		os.MkdirAll(filepath.Dir(dest), 0o755)
		b, _ := os.ReadFile(p)
		os.WriteFile(dest, b, 0o644)
		n++
		return nil
	})
	ok(fmt.Sprintf("%d community file(s)", n))
	return nil
}

// patches\extra\<Project>\*.patch - the lane for changes this kit did not ship with, applied in
// filename order, so prefix them (10-, 20-) when order matters.
//
// IT RUNS AFTER addOurFiles, AND THAT IS THE POINT. It used to run at the end of applyPatches,
// with the kit's own series - which meant it could only touch files that came out of the
// DECOMPILE, because addOurFiles then wrote newfiles\ over the tree. A patch against one of the
// port's own files - anything under UWGame\Mods\, say - reported "applied" and was overwritten a
// step later without a word; on a clean tree the same patch failed the opposite way, "missing
// target", because the file was not there yet. Both are the same bug seen from either side of a
// stale tree. The kit's own series still runs before all of this: it has to land on the pristine
// decompile.
func applyCommunityPatches(o *opts) error {
	root := filepath.Join(o.here, "patches", "extra")
	if _, err := os.Stat(root); err != nil {
		return nil
	}
	started := false
	for _, m := range mappings {
		proj := filepath.Join(o.work, m.Proj)
		extra := filepath.Join(root, m.Asm)
		ents, err := os.ReadDir(extra)
		if err != nil {
			continue
		}
		names := []string{}
		for _, e := range ents {
			if strings.HasSuffix(e.Name(), ".patch") {
				names = append(names, e.Name())
			}
		}
		sort.Strings(names)
		if len(names) > 0 && !started {
			step("Applying community patches")
			started = true
		}
		for _, nm := range names {
			n, probs := applyUnified(filepath.Join(extra, nm), proj)
			if len(probs) > 0 {
				for _, s := range probs[:min(3, len(probs))] {
					fmt.Println("       " + s)
				}
				return fmt.Errorf("community patch %s did not apply. It was not shipped with this kit - remove it from patches\\extra to build without it", nm)
			}
			ok(fmt.Sprintf("extra: %s (%d file(s))", nm, n))
		}
	}
	return nil
}

func addOurFiles(o *opts) error {
	step("Adding the port's own files")
	n := 0
	for _, sub := range []string{"newfiles", "projects"} {
		root := filepath.Join(o.here, sub, "src")
		filepath.Walk(root, func(p string, fi os.FileInfo, err error) error {
			if err != nil || fi.IsDir() {
				return nil
			}
			rel := strings.TrimPrefix(strings.TrimPrefix(p, filepath.Join(o.here, sub)), `\`)
			dest := filepath.Join(o.work, rel)
			os.MkdirAll(filepath.Dir(dest), 0o755)
			b, _ := os.ReadFile(p)
			os.WriteFile(dest, b, 0o644)
			n++
			return nil
		})
	}
	for _, f := range []string{"Directory.Build.props", "Directory.Packages.props", "NuGet.config", "global.json"} {
		if b, err := os.ReadFile(filepath.Join(o.here, f)); err == nil {
			os.WriteFile(filepath.Join(o.work, f), b, 0o644)
		}
	}
	os.MkdirAll(filepath.Join(o.work, ".config"), 0o755)
	if b, err := os.ReadFile(filepath.Join(o.here, `.config\dotnet-tools.json`)); err == nil {
		os.WriteFile(filepath.Join(o.work, `.config\dotnet-tools.json`), b, 0o644)
	}
	copyTree(filepath.Join(o.here, "tools"), filepath.Join(o.work, "tools"))
	ok(fmt.Sprintf("%d file(s), build system in place", n))
	return nil
}

// ---------------------------------------------------------------- 4. proxies, build, assets

const stubRegistry = `// Placeholder registry, seeded to bootstrap a tree with no generated proxies.
// It is overwritten with the real one later in this same run.

using System;
using System.Collections.Generic;

namespace UWGame.Generated.XmlProxies;

internal static class XmlProxyRegistry
{
	internal static readonly Dictionary<Type, Type> ProxyTypes = new();
}
`

func genProxies(o *opts) error {
	step("Generating the XML serialization proxies")
	proxyDir := filepath.Join(o.work, `src\UnclaimedWorld\Generated\XmlProxies`)
	os.MkdirAll(proxyDir, 0o755)
	reg := filepath.Join(proxyDir, "XmlProxyRegistry.g.cs")
	if _, err := os.Stat(reg); err != nil {
		// Two-phase, and it has to be: CustomXmlSerializer references XmlProxyRegistry
		// directly, so the tree cannot compile without it - but the generator produces it by
		// reflecting over the BUILT game. Seed an empty one, build, generate, rebuild.
		os.WriteFile(reg, []byte(stubRegistry), 0o644)
		ok("seeded an empty registry to bootstrap")
	}

	gameProj := filepath.Join(o.work, `src\UnclaimedWorld\UnclaimedWorld.csproj`)
	fmt.Println("       phase 1: building so the generator has something to reflect over...")
	if err := stream(o.dotnet, o.work, "build", gameProj, "-c", "Release", "-p:UwPlatform=DX", "-v", "q", "--nologo"); err != nil {
		return fmt.Errorf("phase-1 build failed: %w", err)
	}

	// Reconstruct ProxyCodeGenerator.cs from YOUR decompiled CustomXmlSerializer.cs. That file
	// is the game's own CodeDom generator, which the port moved out of the game - it is the
	// studio's code, so the kit carries a diff rather than the file. The source must be the
	// UNPATCHED decompile: the patch series deletes those lines from CustomXmlSerializer.cs.
	pcgPatch := filepath.Join(o.here, `patches\ProxyCodeGenerator.patch`)
	if _, err := os.Stat(pcgPatch); err == nil {
		cxs := filepath.Join(o.work, `decomp\UnclaimedWorld\UWGame\SimSide\CustomXmlSerializer.cs`)
		b, err := os.ReadFile(cxs)
		if err != nil {
			return fmt.Errorf("decompiled CustomXmlSerializer.cs not found")
		}
		dstDir := filepath.Join(o.work, `tools\XmlProxyGen`)
		os.MkdirAll(dstDir, 0o755)
		if err := os.WriteFile(filepath.Join(dstDir, "ProxyCodeGenerator.cs"), stripCR(b), 0o644); err != nil {
			return err
		}
		// The patch renames as it transforms; point its +++ header at the destination name.
		pb, _ := os.ReadFile(pcgPatch)
		fixed := regexp.MustCompile(`(?m)^\+\+\+ b/.*$`).ReplaceAll(pb, []byte("+++ b/ProxyCodeGenerator.cs"))
		tmp := filepath.Join(o.work, "pcg.patch")
		os.WriteFile(tmp, fixed, 0o644)
		_, probs := applyUnified(tmp, dstDir)
		os.Remove(tmp)
		if len(probs) > 0 {
			return fmt.Errorf("could not reconstruct ProxyCodeGenerator.cs: %s", probs[0])
		}
		ok("ProxyCodeGenerator.cs reconstructed from your own decompile")
	}

	bin := filepath.Join(o.work, `artifacts\bin\UnclaimedWorld\release_dx`)
	if err := stream(o.dotnet, o.work, "build", filepath.Join(o.work, `tools\XmlProxyGen\XmlProxyGen.csproj`),
		"-c", "Release", "-v", "q", "--nologo", "-p:UwGameBin="+bin); err != nil {
		return fmt.Errorf("could not build XmlProxyGen: %w", err)
	}
	gen := findFile(filepath.Join(o.work, `artifacts\bin\XmlProxyGen`), "xmlproxygen.exe")
	if gen == "" {
		return fmt.Errorf("xmlproxygen.exe not found")
	}
	// The generator LOADS the game assembly, so the real runtime assemblies must sit beside it.
	// Steamworks.NET is the one that matters: the copy at the build-output root is a reference
	// assembly and throws "Cannot load a reference assembly for execution"; the usable one is
	// under runtimes\win-x64, which only a publish puts there.
	stream(o.dotnet, o.work, "publish", gameProj, "-c", "Release", "-p:UwPlatform=DX", "-v", "q", "--nologo")
	copyGlob(bin, "*.dll", filepath.Dir(gen))
	if rt := findFile(filepath.Join(o.work, `artifacts\publish\UnclaimedWorld\release_dx\runtimes\win-x64`), "Steamworks.NET.dll"); rt != "" {
		copyFile(rt, filepath.Join(filepath.Dir(gen), "Steamworks.NET.dll"))
	}
	fmt.Println("       phase 2: generating the real proxies...")
	if err := stream(gen, o.work, proxyDir); err != nil {
		return fmt.Errorf("proxy generation failed: %w", err)
	}
	ok(fmt.Sprintf("%d proxy file(s)", countFiles(proxyDir, ".g.cs")))
	return nil
}

func build(o *opts) error {
	step(fmt.Sprintf("Building (harmony=%v, unhiddenmod=%v)", o.harmony, o.mod))
	// Two booleans, not a list: a list property cannot be passed through the dotnet CLI, which
	// claims both ; and , as its own separators and fails with MSB1006.
	err := stream(o.dotnet, o.work, "publish", filepath.Join(o.work, `src\UnclaimedWorld\UnclaimedWorld.csproj`),
		"-c", "Release", "-p:UwPlatform=DX",
		"-p:UwHarmony="+strconv.FormatBool(o.harmony),
		"-p:UwUnhiddenMod="+strconv.FormatBool(o.mod),
		"-v", "q", "--nologo")
	if err != nil {
		return fmt.Errorf("build failed: %w", err)
	}
	ok("built")
	return nil
}

func stage(o *opts) error {
	step("Staging the port")
	pub := filepath.Join(o.work, `artifacts\publish\UnclaimedWorld\release_dx`)
	os.MkdirAll(o.out, 0o755)

	// Clear the binaries this step owns before copying, or a rebuild with fewer features leaves
	// the previous run's files behind - building with -no-harmony still shipped 0Harmony.dll,
	// which makes "not compiled in" a lie.
	//
	// Root-level FILES and runtimes\ only. Emphatically NOT a recursive delete of the output
	// directory: Content\ may be a junction to the player's real game folder, and a recursive
	// delete that follows it is exactly the accident this kit warns users about.
	if ents, err := os.ReadDir(o.out); err == nil {
		for _, e := range ents {
			if !e.IsDir() {
				os.Remove(filepath.Join(o.out, e.Name()))
			} else if e.Name() == "runtimes" {
				os.RemoveAll(filepath.Join(o.out, e.Name()))
			}
		}
	}

	ents, err := os.ReadDir(pub)
	if err != nil {
		return fmt.Errorf("no build output at %s", pub)
	}
	n := 0
	for _, e := range ents {
		if e.IsDir() {
			if e.Name() == "runtimes" {
				copyTree(filepath.Join(pub, e.Name()), filepath.Join(o.out, e.Name()))
			}
			continue
		}
		if strings.HasSuffix(e.Name(), ".pdb") {
			continue
		}
		copyFile(filepath.Join(pub, e.Name()), filepath.Join(o.out, e.Name()))
		n++
	}
	ok(fmt.Sprintf("%d file(s)", n))
	return nil
}

// shaders converts the 19 effects into port-content\, which the game loads in preference to
// Content\ - so the player's own Content\ is never written to. Only the container framing
// changes; every byte of compiled shader bytecode is copied through untouched.
func shaders(o *opts) error {
	step("Converting the shaders into port-content")
	tool := findFile(filepath.Join(o.work, `artifacts\bin\MgfxTranscode`), "mgfxtranscode.exe")
	if tool == "" {
		if err := stream(o.dotnet, o.work, "build", filepath.Join(o.work, `tools\MgfxTranscode\MgfxTranscode.csproj`),
			"-c", "Release", "-v", "q", "--nologo"); err != nil {
			return err
		}
		tool = findFile(filepath.Join(o.work, `artifacts\bin\MgfxTranscode`), "mgfxtranscode.exe")
	}
	if tool == "" {
		return fmt.Errorf("mgfxtranscode.exe not found")
	}
	override := filepath.Join(o.out, "port-content")
	os.RemoveAll(override)
	os.MkdirAll(override, 0o755)
	srcContent := filepath.Join(o.game, "Content")
	n := 0
	filepath.Walk(srcContent, func(p string, fi os.FileInfo, err error) error {
		if err != nil || fi.IsDir() || !strings.EqualFold(filepath.Ext(p), ".xnb") {
			return nil
		}
		// An effect .xnb names EffectReader near the top. The FULL name matters:
		// "EffectReader" is a substring of "SoundEffectReader", and a loose match would pull in
		// all 220 sound effects.
		f, err := os.Open(p)
		if err != nil {
			return nil
		}
		head := make([]byte, 4096)
		k, _ := io.ReadFull(f, head)
		f.Close()
		if !strings.Contains(string(head[:k]), "Microsoft.Xna.Framework.Content.EffectReader") {
			return nil
		}
		rel := strings.TrimPrefix(strings.TrimPrefix(p, srcContent), `\`)
		dest := filepath.Join(override, rel)
		os.MkdirAll(filepath.Dir(dest), 0o755)
		copyFile(p, dest)
		n++
		return nil
	})
	if err := stream(tool, o.work, "transcode", override); err != nil {
		return err
	}
	stream(tool, o.work, "validate", override)
	ok(fmt.Sprintf("%d effect(s) converted; your Content\\ untouched", n))
	return nil
}

func animation(o *opts) error {
	step("Menu background animation")
	if !o.anim || o.ffmpeg == "" {
		warn("skipped - the still background image will be used")
		return nil
	}
	wmv := filepath.Join(o.game, `Content\MainMenu\TauCetiMainMenu.wmv`)
	if _, err := os.Stat(wmv); err != nil {
		warn("TauCetiMainMenu.wmv not found - using the still image")
		return nil
	}
	return makeAnimation(o, wmv, filepath.Join(o.out, "MainMenuIntro.uwanim"))
}

// ---------------------------------------------------------------- assets
//
// Content\ can be a junction: the port never writes to it, since the converted shaders live in
// port-content\. data\ must be a real copy, because --export-data writes into data\BaseData\ and
// a junction would put that inside the player's real game folder.
//
// Junctions are used rather than symlinks because mklink /J needs no administrator rights,
// whereas a directory symlink does unless Developer Mode is on.

func assets(o *opts) error {
	step("Game assets")
	os.MkdirAll(o.out, 0o755)
	mode := o.assets
	if mode == "auto" {
		if junctionsWork(o.out) {
			ok("this filesystem supports junctions - Content can be linked instead of copied")
			mode = "link"
		} else {
			warn("this filesystem cannot hold junctions (exFAT and FAT32 cannot)")
			fmt.Println("       Content must therefore be copied: 317 MB, plus 102 MB for data.")
			if confirm(o, "Copy the game assets (about 420 MB)?") {
				mode = "copy"
			} else {
				mode = "none"
			}
		}
	}
	dstContent := filepath.Join(o.out, "Content")
	dstData := filepath.Join(o.out, "data")
	if mode == "none" {
		warn("skipping assets")
		fmt.Printf("       Put these in %s yourself before running:\n", o.out)
		fmt.Println("           Content\\   copy or junction of your game's Content")
		fmt.Println("           data\\      COPY of your game's data - it is written to, do not link it")
		return nil
	}
	exec.Command("cmd", "/c", "rmdir", dstContent).Run()
	os.RemoveAll(dstContent)
	if mode == "link" {
		if err := exec.Command("cmd", "/c", "mklink", "/J", dstContent, filepath.Join(o.game, "Content")).Run(); err != nil {
			return fmt.Errorf("could not create the Content junction: %w", err)
		}
		ok("Content\\  -> junction to your game (0 bytes copied, never written to)")
		fmt.Printf("       To remove it later:  cmd /c rmdir \"%s\"\n", dstContent)
		fmt.Println("       rmdir removes the junction only. Do not point a recursive delete at this")
		fmt.Println("       folder: a tool that FOLLOWS junctions could delete your real game content.")
	} else {
		fmt.Println("       copying Content\\ (317 MB)...")
		if err := copyTree(filepath.Join(o.game, "Content"), dstContent); err != nil {
			return err
		}
		ok("Content\\ copied")
	}
	// data\ used to be copied whole, 102 MB, on the grounds that --export-data writes into it.
	// That was true but far too blunt, and it made the result look arbitrary: Content linked,
	// data copied, no visible reason. Measured, only ONE part of data\ is ever written:
	//
	//     data\BaseData    1 KB    --export-data writes here
	//     data\Maps      102 MB    read-only
	//
	// So in link mode data\ becomes a real directory holding junctions to everything read-only,
	// with just BaseData copied. That is 1 KB instead of 102 MB, nothing writes into the game
	// folder, and the rule is now explainable: whatever the game writes to is copied, everything
	// else is linked.
	if err := removeDirOrJunction(dstData); err != nil {
		return err
	}
	if mode == "link" {
		os.MkdirAll(dstData, 0o755)
		ents, err := os.ReadDir(filepath.Join(o.game, "data"))
		if err != nil {
			return err
		}
		linked, copied := 0, 0
		for _, e := range ents {
			src := filepath.Join(o.game, "data", e.Name())
			dst := filepath.Join(dstData, e.Name())
			if !e.IsDir() {
				copyFile(src, dst)
				copied++
				continue
			}
			if strings.EqualFold(e.Name(), "BaseData") {
				if err := copyTree(src, dst); err != nil {
					return err
				}
				copied++
				continue
			}
			if err := exec.Command("cmd", "/c", "mklink", "/J", dst, src).Run(); err != nil {
				// A junction failing for one subdirectory is not worth aborting over; copy it.
				if err := copyTree(src, dst); err != nil {
					return err
				}
				copied++
				continue
			}
			linked++
		}
		ok(fmt.Sprintf("data\\  -> %d linked, %d copied (only what the game writes to is copied)", linked, copied))
	} else {
		fmt.Println("       copying data\\ (102 MB)...")
		if err := copyTree(filepath.Join(o.game, "data"), dstData); err != nil {
			return err
		}
		ok("data\\ copied")
	}
	return nil
}

// steamNative supplies the pieces Steam integration needs beside the executable. Without them
// the game starts but logs "[Steamworks.NET] SteamAPI_Init() failed. Achievements will not be
// available." even with Steam running - which is what happens when the managed wrapper has no
// native library to call into.
//
// Two separate things were missing:
//
//	steam_api64.dll   The kit shipped only the MANAGED Steamworks.NET.dll. The native half has
//	                  to come from somewhere, and it cannot be the game's own copy: that is
//	                  Steamworks SDK ~1.34 (206 KB) and lacks three exports the current
//	                  Steamworks.NET needs, so managed and native must be upgraded together.
//	                  Fetched from the Steamworks.NET release that matches the pinned package.
//
//	steam_appid.txt   Needed for Steam to attach when the game is NOT launched through Steam,
//	                  which is exactly how this port runs. It was only copied on the asset
//	                  paths, so -assets none produced a build that could never see Steam.
//
// Neither is fatal: the game degrades to no achievements and says so.
func steamNative(o *opts) error {
	step("Steam integration")

	if b, err := os.ReadFile(filepath.Join(o.game, "steam_appid.txt")); err == nil {
		os.WriteFile(filepath.Join(o.out, "steam_appid.txt"), b, 0o644)
		ok("steam_appid.txt (from your installation)")
	} else {
		warn("steam_appid.txt not found in your game folder - Steam will not attach")
	}

	dst := filepath.Join(o.out, "steam_api64.dll")
	if _, err := os.Stat(dst); err == nil {
		ok("steam_api64.dll already present")
		return nil
	}

	// Try YOUR game's own copy first, and only download if it is genuinely too old.
	//
	// The reasonable question is "why download something the game already has?" - so this
	// checks rather than assumes. Your installation ships Steamworks SDK ~1.34 (206 KB), and
	// the current Steamworks.NET P/Invokes three entry points it does not export:
	//
	//     SteamInternal_SteamAPI_Init      MISSING in the game's copy
	//     SteamInternal_CreateInterface    MISSING
	//     SteamAPI_ManualDispatch_Init     MISSING
	//
	// Using it would not merely lose achievements - it would throw EntryPointNotFoundException.
	// But that is a property of THIS version of the game, not a law, so the test is on the file:
	// if a copy exports what is needed, it is used and nothing is downloaded.
	//
	// Not scavenged from other installed games, though a newer copy often sits in one: the Steam
	// CLIENT ships no steam_api64.dll at all (it is a per-game redistributable), so any other
	// copy belongs to an unrelated publisher, at an unknown version, and would only work for
	// people who happen to own the right second game.
	needed := []string{"SteamInternal_SteamAPI_Init", "SteamInternal_CreateInterface", "SteamAPI_ManualDispatch_Init"}
	local := filepath.Join(o.game, "steam_api64.dll")
	if b, err := os.ReadFile(local); err == nil {
		missing := []string{}
		for _, n := range needed {
			if !strings.Contains(string(b), n) {
				missing = append(missing, n)
			}
		}
		if len(missing) == 0 {
			copyFile(local, dst)
			ok("steam_api64.dll taken from your own installation (it exports what we need)")
			return nil
		}
		warn(fmt.Sprintf("your game's steam_api64.dll is too old - missing %d export(s), e.g. %s", len(missing), missing[0]))
		fmt.Println("       Steamworks.NET calls those directly, so it cannot be used as-is.")
	}

	const ver = "2024.8.0" // must match the Steamworks.NET package version the port builds against
	url := fmt.Sprintf("https://github.com/rlabrecque/Steamworks.NET/releases/download/%s/Steamworks.NET-Standalone_%s.zip", ver, ver)
	if !confirm(o, "Download the matching Steam native library for achievements (~3 MB)?") {
		warn("skipped - the game will run without achievements and log that it did")
		return nil
	}
	cache := filepath.Join(o.here, "prereqs", "steamworks.zip")
	os.MkdirAll(filepath.Dir(cache), 0o755)
	if _, err := os.Stat(cache); err != nil {
		fmt.Println("       " + url)
		resp, err := http.Get(url)
		if err != nil {
			warn("download failed: " + err.Error() + " - continuing without achievements")
			return nil
		}
		defer resp.Body.Close()
		if resp.StatusCode != 200 {
			warn(fmt.Sprintf("download returned HTTP %d - continuing without achievements", resp.StatusCode))
			return nil
		}
		f, err := os.Create(cache)
		if err != nil {
			return err
		}
		io.Copy(f, resp.Body)
		f.Close()
	}
	zr, err := zip.OpenReader(cache)
	if err != nil {
		warn("could not read the Steamworks archive - continuing without achievements")
		return nil
	}
	defer zr.Close()
	for _, ze := range zr.File {
		if !strings.EqualFold(filepath.Base(ze.Name), "steam_api64.dll") {
			continue
		}
		rc, err := ze.Open()
		if err != nil {
			break
		}
		w, err := os.Create(dst)
		if err != nil {
			rc.Close()
			break
		}
		io.Copy(w, rc)
		w.Close()
		rc.Close()
		fi, _ := os.Stat(dst)
		ok(fmt.Sprintf("steam_api64.dll (%d KB, matches Steamworks.NET %s)", fi.Size()/1024, ver))
		return nil
	}
	warn("steam_api64.dll not found in the archive - continuing without achievements")
	return nil
}

// junctionsWork establishes support by CREATING one, not by reading the filesystem name: the
// answer depends on the volume, and exFAT/FAT32 cannot hold reparse points at all.
//
// The mode test checks ModeIrregular as well as ModeSymlink, because Go reports a junction as
// ModeIrregular - checking only ModeSymlink answers "unsupported" on NTFS, which would send
// every user down the 420 MB copy path.
func junctionsWork(dir string) bool {
	target := filepath.Join(dir, ".uwkit-probe-target")
	link := filepath.Join(dir, ".uwkit-probe-link")
	os.MkdirAll(target, 0o755)
	defer os.RemoveAll(target)
	defer exec.Command("cmd", "/c", "rmdir", link).Run()
	if err := exec.Command("cmd", "/c", "mklink", "/J", link, target).Run(); err != nil {
		return false
	}
	fi, err := os.Lstat(link)
	return err == nil && fi.Mode()&(os.ModeSymlink|os.ModeIrregular) != 0
}

// ---------------------------------------------------------------- unified diff applier
//
// Neither GNU patch nor git ships with Windows, and the kit is meant to work on a plain machine
// with nothing but the .NET SDK. This applies the kit's own patches instead.
//
// Deliberately STRICT: context must match exactly, no fuzz, no offset search. The recipient's
// decompile is byte-identical to the one the patch was generated against - same pinned ilspycmd,
// same game version, both checked before we get here - so a mismatch means something real that
// guessing would only hide.

type hunk struct {
	oldStart, oldLen int
	body             []string
}

func applyUnified(patchFile, root string) (int, []string) {
	data, err := os.ReadFile(patchFile)
	if err != nil {
		return 0, []string{err.Error()}
	}
	lines := strings.Split(strings.ReplaceAll(string(data), "\r\n", "\n"), "\n")
	hdr := regexp.MustCompile(`^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@`)
	var probs []string
	files := 0

	for i := 0; i < len(lines); {
		if !strings.HasPrefix(lines[i], "--- ") || i+1 >= len(lines) || !strings.HasPrefix(lines[i+1], "+++ ") {
			i++
			continue
		}
		rel := strings.TrimSpace(strings.TrimPrefix(strings.TrimPrefix(lines[i+1], "+++ "), "b/"))
		i += 2
		path := filepath.Join(root, filepath.FromSlash(rel))
		raw, err := os.ReadFile(path)
		if err != nil {
			probs = append(probs, "missing target: "+rel)
			continue
		}
		text := string(raw)
		endsNL := strings.HasSuffix(text, "\n")
		content := strings.Split(text, "\n")
		if endsNL && len(content) > 0 {
			content = content[:len(content)-1]
		}

		var hunks []hunk
		for i < len(lines) {
			mm := hdr.FindStringSubmatch(lines[i])
			if mm == nil {
				break
			}
			oldStart, _ := strconv.Atoi(mm[1])
			oldLen := 1
			if mm[2] != "" {
				oldLen, _ = strconv.Atoi(mm[2])
			}
			newLen := 1
			if mm[4] != "" {
				newLen, _ = strconv.Atoi(mm[4])
			}
			i++
			// Bounded by the DECLARED COUNTS, not by a regex on the line prefix: the next
			// file's "--- a/..." header begins with '-', so a prefix-driven loop swallows it as
			// a removed line and every later hunk lands at the wrong offset.
			var body []string
			so, sn := 0, 0
			for i < len(lines) && (so < oldLen || sn < newLen) {
				l := lines[i]
				if strings.HasPrefix(l, `\`) {
					i++
					continue
				}
				if l == "" {
					break
				}
				switch l[0] {
				case ' ':
					so++
					sn++
				case '-':
					so++
				case '+':
					sn++
				default:
					so, sn = oldLen, newLen
					continue
				}
				body = append(body, l)
				i++
			}
			hunks = append(hunks, hunk{oldStart, oldLen, body})
		}

		// Bottom-up, so earlier edits do not shift the line numbers of later ones.
		failed := false
		for h := len(hunks) - 1; h >= 0; h-- {
			hk := hunks[h]
			at := hk.oldStart - 1
			probe := at
			for _, b := range hk.body {
				if b[0] == '+' {
					continue
				}
				want := b[1:]
				if probe >= len(content) || content[probe] != want {
					got := "<eof>"
					if probe < len(content) {
						got = content[probe]
					}
					probs = append(probs, fmt.Sprintf("%s : hunk at line %d does not match (expected %q, found %q)", rel, hk.oldStart, want, got))
					failed = true
					break
				}
				probe++
			}
			if failed {
				break
			}
			var repl []string
			for _, b := range hk.body {
				if b[0] == ' ' || b[0] == '+' {
					repl = append(repl, b[1:])
				}
			}
			out := append([]string{}, content[:at]...)
			out = append(out, repl...)
			out = append(out, content[at+hk.oldLen:]...)
			content = out
		}
		if failed {
			continue
		}
		outText := strings.Join(content, "\n")
		if endsNL {
			outText += "\n"
		}
		if err := os.WriteFile(path, []byte(outText), 0o644); err != nil {
			probs = append(probs, err.Error())
			continue
		}
		files++
	}
	return files, probs
}

// ---------------------------------------------------------------- menu animation
//
// MainMenuIntro.uwanim: a motion-JPEG frame sequence the game plays without any video decoder.
// MonoGame's bundled StbImageSharp already decodes JPEG, so this needs no new dependency and
// works on every backend - whereas the shipped WMV needs MediaFoundation and has no DesktopGL
// path at all.
//
// Container, little-endian: "UWANIM01", int32 version, width, height, count, delayMs,
// int32[count] frame lengths, then the JPEG frames back to back.
func makeAnimation(o *opts, srcVideo, dest string) error {
	tmp, err := os.MkdirTemp("", "uwanim")
	if err != nil {
		return err
	}
	defer os.RemoveAll(tmp)
	const w, h, fps, q = 854, 480, 12, 4
	fmt.Printf("       extracting frames at %dx%d @ %dfps ...\n", w, h, fps)
	cmd := exec.Command(o.ffmpeg, "-nostdin", "-v", "error", "-y", "-i", srcVideo,
		"-vf", fmt.Sprintf("fps=%d,scale=%d:%d", fps, w, h), "-q:v", strconv.Itoa(q),
		filepath.Join(tmp, "%05d.jpg"))
	if err := cmd.Run(); err != nil {
		warn("ffmpeg failed - the still image will be used")
		return nil
	}
	ents, _ := os.ReadDir(tmp)
	names := []string{}
	for _, e := range ents {
		if strings.HasSuffix(e.Name(), ".jpg") {
			names = append(names, e.Name())
		}
	}
	sort.Strings(names)
	if len(names) == 0 {
		warn("ffmpeg produced no frames - the still image will be used")
		return nil
	}
	blobs := make([][]byte, 0, len(names))
	for _, n := range names {
		b, err := os.ReadFile(filepath.Join(tmp, n))
		if err != nil {
			return err
		}
		blobs = append(blobs, b)
	}
	f, err := os.Create(dest)
	if err != nil {
		return err
	}
	defer f.Close()
	f.WriteString("UWANIM01")
	for _, v := range []int32{1, w, h, int32(len(blobs)), int32(1000 / fps)} {
		binary.Write(f, binary.LittleEndian, v)
	}
	for _, b := range blobs {
		binary.Write(f, binary.LittleEndian, int32(len(b)))
	}
	for _, b := range blobs {
		f.Write(b)
	}
	fi, _ := f.Stat()
	ok(fmt.Sprintf("%d frames, %.2f MB -> %s", len(blobs), float64(fi.Size())/(1<<20), filepath.Base(dest)))
	return nil
}

// ---------------------------------------------------------------- Win32: file version
//
// cmd.exe cannot read a file version at all - wmic is deprecated and absent from current
// Windows 11 - and this is the check that catches a wrong game version before anything else
// goes wrong. Done with a lazy DLL bind so no x/sys dependency is needed.

var versionDLL = syscall.NewLazyDLL("version.dll")

func fileVersion(path string) (string, error) {
	p, err := syscall.UTF16PtrFromString(path)
	if err != nil {
		return "", err
	}
	sz, _, _ := versionDLL.NewProc("GetFileVersionInfoSizeW").Call(uintptr(unsafe.Pointer(p)), 0)
	if sz == 0 {
		return "", fmt.Errorf("no version resource")
	}
	buf := make([]byte, sz)
	if r, _, _ := versionDLL.NewProc("GetFileVersionInfoW").Call(
		uintptr(unsafe.Pointer(p)), 0, sz, uintptr(unsafe.Pointer(&buf[0]))); r == 0 {
		return "", fmt.Errorf("GetFileVersionInfoW failed")
	}
	var fixed uintptr
	var flen uint32
	sub, _ := syscall.UTF16PtrFromString(`\`)
	if r, _, _ := versionDLL.NewProc("VerQueryValueW").Call(
		uintptr(unsafe.Pointer(&buf[0])), uintptr(unsafe.Pointer(sub)),
		uintptr(unsafe.Pointer(&fixed)), uintptr(unsafe.Pointer(&flen))); r == 0 || fixed == 0 {
		return "", fmt.Errorf("VerQueryValueW failed")
	}
	ms := *(*uint32)(unsafe.Pointer(fixed + 8))  // VS_FIXEDFILEINFO.dwFileVersionMS
	ls := *(*uint32)(unsafe.Pointer(fixed + 12)) // .dwFileVersionLS
	return fmt.Sprintf("%d.%d.%d.%d", ms>>16, ms&0xffff, ls>>16, ls&0xffff), nil
}

// ---------------------------------------------------------------- plumbing

func parseFlags() *opts {
	o := &opts{assets: "auto", harmony: true, mod: true, anim: true}
	exe, _ := os.Executable()
	o.here = filepath.Dir(exe)
	if wd, err := os.Getwd(); err == nil {
		// Running from a build directory, the exe is not beside the kit; prefer the working
		// directory when it looks like the kit.
		if _, err := os.Stat(filepath.Join(wd, "patches")); err == nil {
			o.here = wd
		}
	}
	args := os.Args[1:]
	for i := 0; i < len(args); i++ {
		next := func() string {
			if i+1 < len(args) {
				i++
				return args[i]
			}
			return ""
		}
		switch strings.TrimLeft(args[i], "-") {
		case "check":
			o.checkOnly = true
		case "y", "yes":
			o.yes = true
		case "game":
			o.game = next()
		case "kit":
			o.here = next()
		case "assets":
			o.assets = next()
		case "no-harmony":
			o.harmony = false
		case "no-mod":
			o.mod = false
		case "no-animation":
			o.anim = false
		case "h", "help":
			usage()
			os.Exit(0)
		}
	}
	if o.work == "" {
		o.work = filepath.Join(o.here, "work")
	}
	os.MkdirAll(o.work, 0o755)
	logPath = filepath.Join(o.work, "build.log")
	os.Remove(logPath)
	if o.out == "" {
		o.out = filepath.Join(o.here, "port")
	}
	return o
}

func usage() {
	fmt.Print(`
uwkit - build the Unclaimed World .NET 8 port from your own copy of the game

  uwkit                 check, ask, then build
  uwkit -check          report what is needed and change nothing
  uwkit -y              accept the prompts

  -game <path>          your installation, if it is not found automatically
  -assets auto|link|copy|none   how Content/data get into the build (default auto)
  -no-harmony           build without the Harmony mod loader
  -no-mod               build without the bundled Unhidden Mod
  -no-animation         do not build the animated menu background

A feature left out is NOT compiled in, not merely disabled.
Your game folder is only ever read.
`)
}

func step(m string) { fmt.Printf("\n==> %s\n", m) }
func ok(m string)   { fmt.Printf("       [ok]   %s\n", m) }
func warn(m string) { fmt.Printf("       [warn] %s\n", m) }

func confirm(o *opts, q string) bool {
	if o.yes {
		fmt.Printf("       %s -> yes (-y)\n", q)
		return true
	}
	if o.checkOnly {
		return false
	}
	fmt.Printf("       %s [y/N] ", q)
	line, _ := bufio.NewReader(os.Stdin).ReadString('\n')
	a := strings.ToLower(strings.TrimSpace(line))
	return a == "y" || a == "yes"
}

func which(name string, fallbacks ...string) string {
	if p, err := exec.LookPath(name); err == nil {
		return p
	}
	for _, f := range fallbacks {
		if f == "" {
			continue
		}
		if _, err := os.Stat(f); err == nil {
			return f
		}
	}
	return ""
}

func run2(exe, dir string, args ...string) (string, error) {
	c := exec.Command(exe, args...)
	c.Dir = dir
	b, err := c.CombinedOutput()
	return string(b), err
}

// stream runs a command, appending its output to work\build.log rather than the console, and
// printing it ONLY if the command fails.
//
// Everything used to go straight to stdout, which meant the decompiler's and MSBuild's warnings
// scrolled past by the hundred - the decompiled source is warning-heavy by nature, and none of
// it is actionable. It read as "all sorts of warnings" and buried the one line that mattered.
// The full output is still kept, because when something does fail that is the first thing to
// look at.
func stream(exe, dir string, args ...string) error {
	c := exec.Command(exe, args...)
	c.Dir = dir
	var buf strings.Builder
	c.Stdout = &buf
	c.Stderr = &buf
	err := c.Run()

	if logPath != "" {
		if f, e := os.OpenFile(logPath, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644); e == nil {
			fmt.Fprintf(f, "\n$ %s %s\n%s", exe, strings.Join(args, " "), buf.String())
			f.Close()
		}
	}
	if err != nil {
		// Only the lines that look like errors, then a pointer to the full log. Dumping the
		// whole thing here is what made the original unreadable.
		shown := 0
		for _, l := range strings.Split(buf.String(), "\n") {
			if strings.Contains(l, " error ") || strings.Contains(strings.ToLower(l), "error:") ||
				strings.Contains(l, ": error") {
				fmt.Println("       " + strings.TrimSpace(l))
				if shown++; shown >= 6 {
					break
				}
			}
		}
		if shown == 0 {
			fmt.Println(indent(tail(buf.String(), 8)))
		}
	}
	return err
}

// logPath is set once the work directory exists; before that there is nowhere to write.
var logPath string

func tail(s string, n int) string {
	lines := strings.Split(strings.TrimRight(s, "\n"), "\n")
	if len(lines) > n {
		lines = lines[len(lines)-n:]
	}
	return strings.Join(lines, "\n")
}

func indent(s string) string {
	out := []string{}
	for _, l := range strings.Split(strings.TrimRight(s, "\n"), "\n") {
		out = append(out, "       "+l)
	}
	return strings.Join(out, "\n")
}

func stripCR(b []byte) []byte {
	out := make([]byte, 0, len(b))
	for i := 0; i < len(b); i++ {
		if b[i] == '\r' && i+1 < len(b) && b[i+1] == '\n' {
			continue
		}
		out = append(out, b[i])
	}
	return out
}

func isGameDir(p string) bool {
	for _, n := range []string{"Content", "data", "UnclaimedWorld.exe"} {
		if _, err := os.Stat(filepath.Join(p, n)); err != nil {
			return false
		}
	}
	return true
}

func findGame() string {
	pf86 := os.Getenv("ProgramFiles(x86)")
	guesses := []string{
		filepath.Join(pf86, `Steam\steamapps\common\Unclaimed World`),
		filepath.Join(os.Getenv("ProgramFiles"), `Steam\steamapps\common\Unclaimed World`),
	}
	// Extra Steam library folders, which is where most people's games actually live.
	if b, err := os.ReadFile(filepath.Join(pf86, `Steam\steamapps\libraryfolders.vdf`)); err == nil {
		for _, m := range regexp.MustCompile(`"path"\s*"([^"]+)"`).FindAllStringSubmatch(string(b), -1) {
			guesses = append(guesses, filepath.Join(strings.ReplaceAll(m[1], `\\`, `\`), `steamapps\common\Unclaimed World`))
		}
	}
	for _, g := range guesses {
		if isGameDir(g) {
			return g
		}
	}
	return ""
}

func findFfmpeg(here string) string {
	local := filepath.Join(here, `prereqs\ffmpeg\bin\ffmpeg.exe`)
	if _, err := os.Stat(local); err == nil {
		return local
	}
	if p, err := exec.LookPath("ffmpeg"); err == nil {
		return p
	}
	// winget's package layout, where most people who have it already have it.
	return findFile(filepath.Join(os.Getenv("LOCALAPPDATA"), `Microsoft\WinGet\Packages`), "ffmpeg.exe")
}

func getFfmpeg(o *opts) error {
	step("Fetching ffmpeg (portable, into prereqs\\ffmpeg)")
	const url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
	dir := filepath.Join(o.here, "prereqs")
	os.MkdirAll(dir, 0o755)
	zipPath := filepath.Join(dir, "ffmpeg.zip")
	fmt.Println("       " + url)
	resp, err := http.Get(url)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	f, err := os.Create(zipPath)
	if err != nil {
		return err
	}
	if _, err := io.Copy(f, resp.Body); err != nil {
		f.Close()
		return err
	}
	f.Close()
	defer os.Remove(zipPath)
	zr, err := zip.OpenReader(zipPath)
	if err != nil {
		return err
	}
	defer zr.Close()
	dest := filepath.Join(dir, "ffmpeg")
	os.RemoveAll(dest)
	for _, ze := range zr.File {
		// The archive has a single versioned top-level folder; flatten it so the path is stable.
		parts := strings.SplitN(strings.ReplaceAll(ze.Name, "/", `\`), `\`, 2)
		if len(parts) < 2 || parts[1] == "" {
			continue
		}
		out := filepath.Join(dest, parts[1])
		if ze.FileInfo().IsDir() {
			os.MkdirAll(out, 0o755)
			continue
		}
		os.MkdirAll(filepath.Dir(out), 0o755)
		rc, err := ze.Open()
		if err != nil {
			return err
		}
		w, err := os.Create(out)
		if err != nil {
			rc.Close()
			return err
		}
		io.Copy(w, rc)
		w.Close()
		rc.Close()
	}
	o.ffmpeg = filepath.Join(dest, `bin\ffmpeg.exe`)
	if _, err := os.Stat(o.ffmpeg); err != nil {
		return fmt.Errorf("ffmpeg.exe not where expected after unpacking")
	}
	ok("ffmpeg -> " + dest)
	return nil
}

func sha256File(p string) (string, error) {
	f, err := os.Open(p)
	if err != nil {
		return "", err
	}
	defer f.Close()
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

func readFirstLine(p string) (string, error) {
	f, err := os.Open(p)
	if err != nil {
		return "", err
	}
	defer f.Close()
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		l := strings.TrimSpace(sc.Text())
		if l != "" && !strings.HasPrefix(l, "#") {
			return l, nil
		}
	}
	return "", fmt.Errorf("no value in %s", p)
}

func countFiles(root, ext string) int {
	n := 0
	filepath.Walk(root, func(p string, fi os.FileInfo, err error) error {
		if err == nil && !fi.IsDir() && strings.HasSuffix(p, ext) {
			n++
		}
		return nil
	})
	return n
}

func findFile(root, name string) string {
	found := ""
	filepath.Walk(root, func(p string, fi os.FileInfo, err error) error {
		if err == nil && !fi.IsDir() && strings.EqualFold(fi.Name(), name) && found == "" {
			found = p
		}
		return nil
	})
	return found
}

func copyFile(src, dst string) error {
	b, err := os.ReadFile(src)
	if err != nil {
		return err
	}
	os.MkdirAll(filepath.Dir(dst), 0o755)
	return os.WriteFile(dst, b, 0o644)
}

func copyGlob(dir, pattern, dst string) {
	ms, _ := filepath.Glob(filepath.Join(dir, pattern))
	for _, m := range ms {
		copyFile(m, filepath.Join(dst, filepath.Base(m)))
	}
}

func copyTree(src, dst string) error {
	return filepath.Walk(src, func(p string, fi os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		rel := strings.TrimPrefix(strings.TrimPrefix(p, src), `\`)
		out := filepath.Join(dst, rel)
		if fi.IsDir() {
			return os.MkdirAll(out, 0o755)
		}
		return copyFile(p, out)
	})
}

func min(a, b int) int {
	if a < b {
		return a
	}
	return b
}

// removeDirOrJunction deletes a directory that may be a junction. rmdir removes a junction
// WITHOUT touching its target; os.RemoveAll on a junction can recurse into the real game folder,
// which is the one accident this kit must never cause.
func removeDirOrJunction(p string) error {
	fi, err := os.Lstat(p)
	if err != nil {
		return nil
	}
	if fi.Mode()&(os.ModeSymlink|os.ModeIrregular) != 0 {
		return exec.Command("cmd", "/c", "rmdir", p).Run()
	}
	return os.RemoveAll(p)
}
