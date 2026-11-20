---
title: Batch conversion and build automation
---

# Batch conversion and build automation

Use the bundled **Converter** directory without starting the LIBERA FX desktop
editor. The converter produces effect packages; Unity/Unreal/Godot importing,
binding and game builds remain separate steps in the
[production workflow](production-workflow.md).

The standalone converter uses the editor's executable package compiler for
Spawnable Groups, CPU/GPU particles and Fluid2D, including nested scopes.
Use matching converter and engine SDK builds. Portable Resource Identity
projects need the diagnostic manifest path below, rather than normal `.lfxb`
batch output.

## Single-file and directory conversion

Examples use Windows PowerShell, from the release's `Converter` directory.
Use the same build of converter and engine SDK:

```powershell
.\LiberaFxConverter.exe --help
.\LiberaFxConverter.exe convert "C:\FX\Source\Spark.lfxproj" "C:\FX\Build\Spark.lfxb"
.\LiberaFxConverter.exe batch "C:\FX\Source" "C:\FX\Build"
```

`batch` recursively finds `.lfxproj`, preserves relative subdirectories and
changes the extension to `.lfxb`. For example:

```text
C:\FX\Source\Combat\Hit.lfxproj
    → C:\FX\Build\Combat\Hit.lfxb
```

Input projects and libraries are not rewritten. Existing outputs cause failure;
`--force` explicitly permits replacing them. Each file is written through a
temporary file, but the **whole batch is not one transaction**: other effects
may already have been written when a conversion fails.

## Convert a list of effects

```powershell
.\LiberaFxConverter.exe batch --list "C:\FX\effects.txt" "C:\FX\Build"
```

Write one `.lfxproj` path per line. Relative paths start at the list file's
directory. Blank lines and lines starting with `#` are ignored. By default,
the output uses the input filename with `.lfxb`; optionally add a TAB followed
by a relative output path, such as `Particles/Fountain.lfxb`.

```text
# effects.txt
Source/Radial.lfxproj
Source/Fountain.lfxproj
Source/Curl.lfxproj
```

`batch-list <list> <output-directory>` is an alias. Duplicate outputs and paths
outside the output directory are rejected before conversion. Conversion
continues after individual file failures and returns a nonzero exit code if
any file fails. Existing files still require `--force`.

## Shared groups and project-specific nodes

Pass the libraries explicitly. The desktop's active project/settings are not
an implicit input to an unattended build:

```powershell
.\LiberaFxConverter.exe batch "C:\FX\Source" "C:\FX\Build" `
    --groups "C:\FX\Libraries\Shared" `
    --groups "C:\FX\Libraries\Project" `
    --nodes "C:\FX\NodeDefinitions" `
    --force
```

| Option | Meaning |
| --- | --- |
| `--groups <directory>` | Repeatable. Resolve linked groups against their latest versions in these libraries. Missing or conflicting definitions fail instead of silently using saved copies. |
| `--embedded-groups` | Deliberately freeze linked groups to their saved, embedded revisions. Use for a pinned-source build, not as an automatic error fallback. |
| `--nodes <directory>` | Repeatable. Load project-specific JSON node definitions. Invalid definitions fail. This does not implement a missing native node or include its engine registration. |
| `--force` | Replace existing target files. It does not delete obsolete outputs or make the command an incremental build. |

For reproducible builds, pin the converter, SDK, projects and group/node
libraries to recorded revisions. “Latest groups” means latest in those supplied
libraries at conversion time; snapshot those inputs before a release.
The desktop export and CLI do not implicitly share the desktop's open profile.

## Integrate with a game build

1. Check out the pinned authoring inputs and create a fresh, separate staging
   directory. Keep source and output trees separate.
2. Run one `batch` invocation with the intended group/node options.
3. **Stop on any nonzero exit code.** Do not publish a partly successful staging
   directory or quietly fall back to last build's packages.
4. On success, publish/copy packages to the engine project and run its ordinary
   import/reimport and binding checks, then cook/build/export the game.
5. Test loading and visible playback without the desktop editor running.

```powershell
.\LiberaFxConverter.exe batch "C:\FX\Source" "C:\BuildStaging\FX" `
    --groups "C:\FX\Libraries\Shared" --force
if ($LASTEXITCODE -ne 0) {
    throw "Effect conversion failed; do not publish these outputs."
}
```

Exit codes are **0** for complete success, **1** for conversion/input/output
failure, and **2** for invalid command usage. An empty input directory fails.
The CLI is not a Unity AssetDatabase, Unreal commandlet or Godot importer.
Use your engine's build tooling after conversion. A fresh staging directory
also prevents deleted or renamed effects from lingering in distribution.

## Resource Identity manifest: not a playable asset

Normal `.lfxb` export rejects identity metadata rather than discarding it.
The separate diagnostic export supports **single-file `convert` only**:

```powershell
.\LiberaFxConverter.exe convert "C:\FX\Mapped.lfxproj" "C:\FX\Build\Mapped.lfxrm" `
    --binding-snapshot "C:\FX\Bindings\snapshot.json" `
    --target-engine Unity --target-project "11111111-2222-4333-8444-555555555555"
```

Replace the example GUID with the exact persistent engine-project identity.
Supply `--resource-map <file>` only if needed; it must agree with the project's
saved identity document. The snapshot contains caller-supplied exact resource
and parameter assertions for that project. It is **not live verification** or
an automatically generated list of guessed name matches.

`.lfxrm` contains mapping diagnostics and compiled data, but cannot currently
be imported as a normal Unity/Unreal/Godot effect. It does not solve mapped
offline package export, and `batch` does not accept mapping export options.
See the [identity format and snapshot specification](https://github.com/Libra-Works/LibraFX/blob/main/docs/resource-identity.txt).

## Working from source

The equivalent repository command is:

```powershell
dotnet run --project Tools\Converter -- batch "C:\FX\Source" "C:\FX\Build" --embedded-groups
```

This requires the repository's .NET SDK/build dependencies. A bundled release
converter does not require opening the editor or using `dotnet run`.
