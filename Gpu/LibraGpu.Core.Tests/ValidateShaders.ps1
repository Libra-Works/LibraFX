param(
    [string]$DxcPath,
    [switch]$KeepArtifacts
)
$ErrorActionPreference = 'Stop'
if (-not $DxcPath) {
    $command = Get-Command dxc -ErrorAction SilentlyContinue
    if ($command) { $DxcPath = $command.Source }
    else {
        $sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
        $DxcPath = Get-ChildItem $sdk -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\dxc.exe' } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
    }
}
if (-not $DxcPath -or -not (Test-Path $DxcPath)) {
    throw 'No installed DXC found. No shaders were compiled or GPU-executed. Supply -DxcPath.'
}
$output = Join-Path $PSScriptRoot 'shader-validation'
$owned = [System.Collections.Generic.List[string]]::new()
try {
    & dotnet run --project (Join-Path $PSScriptRoot 'LibraGpu.Core.Tests.csproj') -- --export $output
    if ($LASTEXITCODE -ne 0) { throw 'Focused GPU tests failed.' }
    $count = 0
    foreach ($name in @('sample', 'allops')) {
        foreach ($extension in @('hlsl', 'usf', 'compute', 'json', 'metadata.json', 'entries')) {
            $owned.Add((Join-Path $output "$name.$extension"))
        }
        foreach ($extension in @('hlsl', 'usf', 'compute')) {
            foreach ($entry in (Get-Content (Join-Path $output "$name.entries"))) {
                $binary = Join-Path $output "$name.$extension.$entry.dxil"
                $owned.Add($binary)
                & $DxcPath -T cs_6_0 -E $entry -Ges -WX -Fo $binary (Join-Path $output "$name.$extension")
                if ($LASTEXITCODE -ne 0) { throw "DXC failed: $name.$extension $entry" }
                $count++
            }
        }
    }
    Write-Output "$count strict DXC shader compilations passed. No runtime GPU execution claimed."
}
finally {
    if (-not $KeepArtifacts) {
        foreach ($file in $owned) { if (Test-Path $file) { Remove-Item $file } }
        if ((Test-Path $output) -and -not (Get-ChildItem $output -Force | Select-Object -First 1)) {
            Remove-Item $output
        }
    }
}
