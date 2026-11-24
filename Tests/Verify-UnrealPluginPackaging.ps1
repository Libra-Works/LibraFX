#Requires -Version 7.0
param([string] $UnrealSdkRoot)

$ErrorActionPreference = 'Stop'
$installer = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Runtime\Integrations\Unreal\Install-LibraRuntimeForUnreal.ps1'))
$tokens = $null
$errors = $null
$null = [Management.Automation.Language.Parser]::ParseFile($installer, [ref] $tokens, [ref] $errors)
if ($errors.Count -ne 0) { throw ($errors | Out-String) }
$root = Join-Path ([IO.Path]::GetTempPath()) ('libra-ue-plugin-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $project = Join-Path $root 'Packaging.uproject'
    [IO.File]::WriteAllText($project, '{"FileVersion":3,"EngineAssociation":"5.7","Modules":[]}')
    $projectHash = (Get-FileHash -LiteralPath $project).Hash
    & $installer -ProjectPath $project
    $plugin = Join-Path $root 'Plugins\LibraRuntimeForUnreal'
    $manifestPath = Join-Path $plugin '.libra-install-manifest.json'
    $manifestText = [IO.File]::ReadAllText($manifestPath)
    $manifest = $manifestText | ConvertFrom-Json
    foreach ($entry in $manifest.Files) {
        $file = Join-Path $plugin $entry.Path
        if ((Get-FileHash -LiteralPath $file).Hash -ne $entry.Hash) { throw "Incorrect packaged bytes: $($entry.Path)" }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $plugin 'Source\ThirdParty\LibraRuntimeCore\Core\LbVfxProgram.cpp')) -or
        -not (Test-Path -LiteralPath (Join-Path $plugin 'Source\ThirdParty\LibraRuntimeCore\Core\LbVfxInstance.cpp'))) {
        throw 'The packaged plugin depends on external repository source files.'
    }
    if ($UnrealSdkRoot) {
        $exportedInstaller = Join-Path $UnrealSdkRoot 'Runtime\Integrations\Unreal\Install-LibraRuntimeForUnreal.ps1'
        & $exportedInstaller -ProjectPath $project -Update
        $copyProject = Join-Path $root 'ManualCopy'
        [IO.Directory]::CreateDirectory((Join-Path $copyProject 'Plugins')) | Out-Null
        Copy-Item -LiteralPath $plugin -Destination (Join-Path $copyProject 'Plugins') -Recurse
        $copy = Join-Path $copyProject 'Plugins\LibraRuntimeForUnreal'
        foreach ($name in 'VERSION.json', 'LICENSE') {
            if ((Get-FileHash -LiteralPath (Join-Path $copy $name)).Hash -ne
                (Get-FileHash -LiteralPath (Join-Path $UnrealSdkRoot $name)).Hash) {
                throw "Manual copy did not retain the exported $name."
            }
        }
        if (Test-Path -LiteralPath (Join-Path $copy '.libra-core-source-path')) {
            throw 'The copy-ready plugin must not contain a development link marker.'
        }
        $copyManifest = Get-Content -LiteralPath (Join-Path $copy '.libra-install-manifest.json') -Raw | ConvertFrom-Json
        foreach ($entry in $copyManifest.Files) {
            if ((Get-FileHash -LiteralPath (Join-Path $copy $entry.Path)).Hash -ne $entry.Hash) {
                throw "The copied plugin file is missing or changed: $($entry.Path)"
            }
        }
    }
    $moduleRules = @(Get-ChildItem (Join-Path $plugin 'Source') -Recurse -Filter '*.Build.cs' |
        ForEach-Object { $_.Name -replace '\.Build\.cs$', '' } | Sort-Object)
    $expectedRules = @('LibraGpuEditor', 'LibraGpuRuntime', 'LibraRuntimeCore',
        'LibraRuntimeForUnreal', 'LibraRuntimeForUnrealEditor') | Sort-Object
    if ($moduleRules.Count -ne $expectedRules.Count -or
        (Compare-Object -ReferenceObject $expectedRules -DifferenceObject $moduleRules)) {
        throw 'RuntimeCore, Runtime and Editor module boundaries were not packaged.'
    }
    $coreSourceDir = Join-Path $plugin 'Source\ThirdParty\LibraRuntimeCore\Core'
    $wrapperDir = Join-Path $plugin 'Source\LibraRuntimeCore\Private'
    $wrapperIncludes = (Get-ChildItem -LiteralPath $wrapperDir -Filter '*.cpp' |
        ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"
    $missingCoreWrappers = @(Get-ChildItem -LiteralPath $coreSourceDir -Recurse -Filter '*.cpp' |
        Where-Object { $_.Name -notmatch '^LbInterop' -and $_.Name -ne 'LbLivePreview.cpp' } |
        Where-Object {
            $relative = [IO.Path]::GetRelativePath($coreSourceDir, $_.FullName).Replace('\', '/')
            $wrapperIncludes -notmatch [regex]::Escape($relative)
        } |
        ForEach-Object { [IO.Path]::GetRelativePath($coreSourceDir, $_.FullName).Replace('\', '/') })
    if ($missingCoreWrappers.Count -gt 0) {
        throw ("LibraRuntimeCore/Private is missing Unreal wrapper .cpp files for: " + ($missingCoreWrappers -join ', '))
    }
    $local = Join-Path $plugin 'LocalNotes.txt'
    [IO.File]::WriteAllText($local, 'Unowned project-local content')
    & $installer -ProjectPath $root -Update
    if ([IO.File]::ReadAllText($local) -ne 'Unowned project-local content') { throw 'An update modified an unowned file.' }
    $owned = Join-Path $plugin 'Source\LibraRuntimeCore\Private\LibraRuntimeCoreNative.cpp'
    $original = [IO.File]::ReadAllText($owned)
    [IO.File]::AppendAllText($owned, "`n// Project-local change`n")
    $modified = [IO.File]::ReadAllText($owned)
    $refused = $false
    try { & $installer -ProjectPath $root -Update }
    catch {
        if ($_.Exception.Message -notlike '*Locally modified plugin file preserved*') { throw }
        $refused = $true
    }
    if (-not $refused -or [IO.File]::ReadAllText($owned) -ne $modified) {
        throw 'An update overwrote a project-modified source file.'
    }
    [IO.File]::WriteAllText($owned, $original, [Text.UTF8Encoding]::new($false))
    $manifestText = [IO.File]::ReadAllText($manifestPath)
    $manifest = $manifestText | ConvertFrom-Json
    $ownedRelative = [IO.Path]::GetRelativePath($plugin, $owned)
    $manifest.Files = @($manifest.Files | Where-Object Path -NE $ownedRelative)
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5))
    [IO.File]::WriteAllText($owned, $modified)
    $refused = $false
    try { & $installer -ProjectPath $root -Update }
    catch {
        if ($_.Exception.Message -notlike '*Unowned plugin file preserved*') { throw }
        $refused = $true
    }
    if (-not $refused -or [IO.File]::ReadAllText($owned) -ne $modified) {
        throw 'An update overwrote an unowned conflicting source file.'
    }
    [IO.File]::WriteAllText($owned, $original, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($manifestPath, $manifestText)
    $manifest = $manifestText | ConvertFrom-Json
    $obsolete = Join-Path $plugin 'Obsolete.generated.cpp'
    [IO.File]::WriteAllText($obsolete, '// Previous installer-generated file')
    $manifest.Files += [pscustomobject]@{ Path = 'Obsolete.generated.cpp'; Hash = (Get-FileHash -LiteralPath $obsolete).Hash }
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5))
    & $installer -ProjectPath $root -Update
    if (Test-Path -LiteralPath $obsolete) { throw 'An unchanged obsolete installer-owned file survived the update.' }
    if (-not (Test-Path -LiteralPath $local)) { throw 'Obsolete cleanup removed an unowned file.' }
    $manifestText = [IO.File]::ReadAllText($manifestPath)
    $manifest = $manifestText | ConvertFrom-Json
    $manifest.Files[0].Path = '..\outside.txt'
    $outside = Join-Path $root 'Plugins\outside.txt'
    [IO.File]::WriteAllText($outside, 'Protected')
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5))
    $refused = $false
    try { & $installer -ProjectPath $root -Update }
    catch {
        if ($_.Exception.Message -notlike '*Path outside plugin rejected*') { throw }
        $refused = $true
    }
    if (-not $refused -or [IO.File]::ReadAllText($outside) -ne 'Protected') {
        throw 'A manifest entry escaped the plugin boundary.'
    }
    [IO.File]::WriteAllText($manifestPath, $manifestText)
    if ((Get-FileHash -LiteralPath $project).Hash -ne $projectHash) { throw 'Installation changed the user project file.' }
    if (Get-ChildItem -LiteralPath (Join-Path $root 'Plugins') -Directory | Where-Object Name -Like '.libra-stage-*') {
        throw 'Plugin staging directories were not cleaned up.'
    }
    $fixtureRuntime = Join-Path $root 'InstallerFixture\Runtime'
    $fixtureIntegration = Join-Path $fixtureRuntime 'Integrations\Unreal'
    [IO.Directory]::CreateDirectory($fixtureIntegration) | Out-Null
    $fixtureInstaller = Join-Path $fixtureIntegration 'Install-LibraRuntimeForUnreal.ps1'
    [IO.File]::Copy($installer, $fixtureInstaller)
    $fixturePlugin = Join-Path $fixtureIntegration 'LibraRuntimeForUnreal'
    $sourcePlugin = Join-Path ([IO.Path]::GetDirectoryName($installer)) 'LibraRuntimeForUnreal'
    [IO.Directory]::CreateDirectory($fixturePlugin) | Out-Null
    [IO.File]::Copy((Join-Path $sourcePlugin 'LibraRuntimeForUnreal.uplugin'), (Join-Path $fixturePlugin 'LibraRuntimeForUnreal.uplugin'))
    foreach ($directory in 'Source', 'Resources') {
        $sourceDirectory = Join-Path $sourcePlugin $directory
        if (Test-Path -LiteralPath $sourceDirectory) {
            Copy-Item -LiteralPath $sourceDirectory -Destination $fixturePlugin -Recurse
        }
    }
    foreach ($directory in 'Core', 'include') {
        Copy-Item -LiteralPath (Join-Path $plugin "Source\ThirdParty\LibraRuntimeCore\$directory") -Destination $fixtureRuntime -Recurse
    }
    [IO.File]::WriteAllText((Join-Path $fixtureRuntime 'Core\PackagingFixture.cpp'), 'int LibraPackagingFixture = 0;')
    $extraProject = Join-Path $root 'AdditionalTranslationUnit.uproject'
    [IO.File]::WriteAllText($extraProject, '{"FileVersion":3}')
    & $fixtureInstaller -ProjectPath $extraProject -Update
    $wrappers = @(Get-ChildItem -LiteralPath (Join-Path $plugin 'Source\LibraRuntimeCore\Private\Generated') -Filter '*.cpp' |
        Where-Object { [IO.File]::ReadAllText($_.FullName).Contains('#include "Core/PackagingFixture.cpp"') })
    if ($wrappers.Count -ne 1 -or -not [IO.File]::ReadAllText($wrappers[0].FullName).Contains('#include "HAL/Platform.h"')) {
        throw 'Additional native sources did not receive separate UE translation-unit wrappers with export macro definitions.'
    }
    $linkProjectRoot = Join-Path $root 'LinkedProject'
    [IO.Directory]::CreateDirectory($linkProjectRoot) | Out-Null
    $linkProject = Join-Path $linkProjectRoot 'Linked.uproject'
    [IO.File]::WriteAllText($linkProject, '{"FileVersion":3}')
    $linkProjectHash = (Get-FileHash -LiteralPath $linkProject).Hash
    & $fixtureInstaller -ProjectPath $linkProject
    $linkedPlugin = Join-Path $linkProjectRoot 'Plugins\LibraRuntimeForUnreal'
    $linkedSource = Join-Path $linkedPlugin 'Source\LibraRuntimeCore\Private\LibraRuntimeCoreNative.cpp'
    [IO.File]::AppendAllText($linkedSource, "`n// User edit")
    $refused = $false
    try { & $fixtureInstaller -ProjectPath $linkProject -Link -Update }
    catch {
        if ($_.Exception.Message -notlike '*Locally modified plugin file preserved*') { throw }
        $refused = $true
    }
    if (-not $refused) { throw 'Link conversion ignored a user edit.' }
    [IO.File]::Copy((Join-Path $fixtureIntegration 'LibraRuntimeForUnreal\Source\LibraRuntimeCore\Private\LibraRuntimeCoreNative.cpp'), $linkedSource, $true)
    $unownedSource = Join-Path $linkedPlugin 'Source\ProjectLocal.cpp'
    [IO.File]::WriteAllText($unownedSource, '// Unowned source')
    $refused = $false
    try { & $fixtureInstaller -ProjectPath $linkProject -Link -Update }
    catch {
        if ($_.Exception.Message -notlike '*Unowned plugin source preserved*') { throw }
        $refused = $true
    }
    if (-not $refused -or -not (Test-Path -LiteralPath $unownedSource)) { throw 'Link conversion ignored an unowned source.' }
    [IO.File]::Delete($unownedSource)
    & $fixtureInstaller -ProjectPath $linkProject -Link -Update
    $link = Get-Item -LiteralPath $linkedPlugin -Force
    if (-not $link.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Development installation is not a directory link.' }
    & $fixtureInstaller -ProjectPath $linkProject -Link
    $fixtureSource = Join-Path $fixtureIntegration 'LibraRuntimeForUnreal'
    if ([IO.File]::ReadAllText((Join-Path $fixtureSource '.libra-core-source-path')) -ne $fixtureRuntime) {
        throw 'Linked native Core does not reference the live source tree.'
    }
    $linkedOriginal = [IO.File]::ReadAllText($linkedSource)
    [IO.File]::AppendAllText((Join-Path $fixtureSource 'Source\LibraRuntimeCore\Private\LibraRuntimeCoreNative.cpp'), "`n// Live fixture edit")
    if ([IO.File]::ReadAllText($linkedSource) -eq $linkedOriginal) { throw 'Source edits did not propagate through the development link.' }
    if (@(Get-ChildItem -LiteralPath $linkProjectRoot -Directory -Force | Where-Object Name -Like '.libra-plugin-backup-*').Count -ne 1) {
        throw 'Previous installation was not retained outside Plugins.'
    }
    $refused = $false
    try { & $fixtureInstaller -ProjectPath $linkProject -Update }
    catch {
        if ($_.Exception.Message -notlike '*Installing through a link is not supported*') { throw }
        $refused = $true
    }
    if (-not $refused) { throw 'Copy update followed a development link.' }
    if ((Get-FileHash -LiteralPath $linkProject).Hash -ne $linkProjectHash) { throw 'Link conversion changed the project descriptor.' }
    [IO.Directory]::Delete($linkedPlugin)
    Write-Output 'Unreal source packaging, owned-file updates, local-edit protection and manifest boundary checks passed.'
}
finally {
    if ([IO.Path]::GetDirectoryName($root) -ne [IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar) -or
        -not [IO.Path]::GetFileName($root).StartsWith('libra-ue-plugin-test-')) {
        throw 'Unexpected test directory; cleanup refused.'
    }
    [IO.Directory]::Delete($root, $true)
}
