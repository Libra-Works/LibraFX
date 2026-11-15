#Requires -Version 7.0
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
    if (-not (Test-Path -LiteralPath (Join-Path $plugin 'Source\ThirdParty\LibraRuntimeCore\Core\VfxRuntime.cpp'))) {
        throw 'The packaged plugin depends on external repository source files.'
    }
    if (@(Get-ChildItem (Join-Path $plugin 'Source') -Recurse -Filter '*.Build.cs').Count -ne 3) {
        throw 'RuntimeCore, Runtime and Editor module boundaries were not packaged.'
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
    Copy-Item -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($installer)) 'LibraRuntimeForUnreal') -Destination $fixtureIntegration -Recurse
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
    Write-Output 'Unreal source packaging, owned-file updates, local-edit protection and manifest boundary checks passed.'
}
finally {
    if ([IO.Path]::GetDirectoryName($root) -ne [IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar) -or
        -not [IO.Path]::GetFileName($root).StartsWith('libra-ue-plugin-test-')) {
        throw 'Unexpected test directory; cleanup refused.'
    }
    [IO.Directory]::Delete($root, $true)
}
