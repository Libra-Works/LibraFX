# Engine SDK source distributions

The SDKs still live in this repository. `Libra-Works/ForUnity` and
`Libra-Works/ForUnreal` have **not** been created or migrated by this tooling.
Repository creation, history migration, removal of the originals, releases and
pushes require a separate approved migration.

Unity, Unreal and the initial Windows x86-64 Godot GDExtension can all be
staged locally with the same tool.

## Reproducible local staging

From PowerShell 7, export into a **new absolute directory outside the repository**:

```powershell
.\Tools\Export-EngineSdk.ps1 -Engine Unity -OutputDirectory C:\SdkStaging\ForUnity
.\Tools\Export-EngineSdk.ps1 -Engine Unreal -OutputDirectory C:\SdkStaging\ForUnreal
.\Tools\Export-EngineSdk.ps1 -Engine Godot -OutputDirectory C:\SdkStaging\ForGodot
```

The parent directory must exist. Existing destinations, links and dirty source
trees are rejected. `-AllowDirty` is only for local development: its manifest
explicitly records that the source does not match the recorded HEAD exactly.
Each file receives a SHA-256 entry in `sdk-manifest.json`. Generated binaries,
logs, installation markers and engine build caches are excluded. The export
never uploads, invokes an engine, builds, or edits an installed project.

Each export preserves `Runtime\Core`, `Runtime\include`, `Runtime\CMakeLists.txt`
and its engine adapter's relative paths. `SDK\compatibility.json` records the
shared ABI, bytecode/package ceiling and actual verification boundaries.
Compiler/editor sources are not runtime dependencies.

## Unity entry point

In Unity Package Manager, use **Add package from disk** and select
`Runtime\Integrations\Unity\package.json` in the export. This is a local package,
not an online package registry. The existing runtime/editor asmdefs separate
editor tooling from player code and enable the unsafe native bridge.
The source-only root asmdef excludes standalone .NET tooling/test sources from
Unity compilation by default; do not enable `LIBERAFX_STANDALONE_DOTNET_TOOLS`
in a Unity project. Run those tools through their own .NET projects instead.

Build `liberafx_native` from the exported Runtime CMake project with tests off,
then install the matching platform native plugin through Unity's plugin importer.
Do not install a Windows DLL for other target platforms or assume desktop dynamic
loading is permitted on consoles. See the included `USAGE.txt` and
`GPU-USAGE.txt` for package import, bindings, initialization, Play, Stop, disposal
and minimal Cube sample generation using the full authoring repository.

Unity 6000.0 is a **declared package minimum**, not a completed Unity 6 acceptance
claim. Previous execution evidence covers the 7000-series URP/D3D11 setup only.
Unity 6, other render pipelines/backends and platform SDKs still need their own
build/playback/resource-retirement acceptance.

## Unreal entry point

Use the exported adapter's `Install-LibraRuntimeForUnreal.ps1` with an explicit
`.uproject`. It copies the selected plugin and Core sources into the project;
`-Update` protects locally modified files. Prefer copy installation for a release;
`-Link` is an explicit local development convenience, not a redistributable SDK.
The installer does not launch Unreal, build, or edit the project's configuration.

Use **ULibraEffectComponent** as the public whole-effect entry point; internal
CPU/GPU/Spawnable helpers do not require author-added companion components.
Read its included `USAGE.txt` for asset import, explicit resource bindings,
Play/Stop, instance I/O and lifetime ownership. UE 5.7+ is the current declared
minimum. Keep the portable Core/Unreal integration Clang-compilable, and use MSVC
for final Windows Unreal integration verification. This does not certify console
SDKs, cooking or gameplay merely because a desktop source build succeeds.

## Godot entry point

Build `Runtime\Integrations\Godot` from the staged source with a separate
`godot-cpp` checkout supplying the 4.7 API. Copy `liberafx.gdextension` and
the matching DLL into the Godot project and add a `LibraEffectPlayer` node.
Convert `.lfxproj` to `.lfxb` using the Converter from the full authoring
repository before copying the bytecode into the project. See the included
`Runtime\Integrations\Godot\USAGE.txt` for exact build, installation, and smoke
test commands.

The Godot adapter accepts standalone CPU bytecode v1 and v2, not Spawnable
packages. It does not expose rendering, Game I/O, asset resolution, GPU or
live preview. Headless playback has been exercised on Godot 4.7.2 Windows
x86-64; exported players have not been verified. When selecting engine
verification targets, include the Godot smoke test for changes that affect its
supported Core, Converter, integration or distribution paths.

## Version and ownership boundary

Native ABI 1, the compiled bytecode version and the package version are distinct.
Keep the editor/converter and SDK revision together, reject unsupported formats,
and regenerate fixtures when the compiler changes. Engine adapters own engine
objects and capture/apply snapshots on their required threads; Core owns portable
execution over caller/host-owned memory. No serialized pointers identify assets
or live instances. Host generation plus container and instance identity protects
targeted operations. Never retain freed state or use a dynamic editor DLL in Core.

An eventual split must preserve the source lock/manifest, carry required notices
and samples, exercise independent installation in each target engine, and agree
on compatibility/release coordination before deleting duplicate sources here.
