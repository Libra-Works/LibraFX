#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$pattern = [regex]::new('!?\[[^\]\r\n]*\]\((?<target>[^\s)]+)(?:\s+"[^"]*")?\)',
    [Text.RegularExpressions.RegexOptions]::None, [TimeSpan]::FromSeconds(1))
$failures = [Collections.Generic.List[string]]::new()
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repository 'docs') -Filter '*.md' -File -Recurse) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    $text = [regex]::Replace($text, '(?s)```.*?```', '', [Text.RegularExpressions.RegexOptions]::None,
        [TimeSpan]::FromSeconds(1))
    foreach ($match in $pattern.Matches($text)) {
        $target = [Uri]::UnescapeDataString(($match.Groups['target'].Value -split '[#?]', 2)[0])
        if (-not $target -or $target -match '^(https?://|mailto:)') { continue }
        if ([IO.Path]::IsPathRooted($target) -or $target -match '^[A-Za-z][A-Za-z0-9+.-]*:') {
            $failures.Add("$($file.Name): non-repository link $target")
            continue
        }
        $path = [IO.Path]::GetFullPath((Join-Path $file.DirectoryName $target))
        if (-not $path.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, $comparison) -or
            -not (Test-Path -LiteralPath $path)) {
            $failures.Add("$($file.Name): missing or out-of-repository link $target")
        }
    }
}
if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
Write-Output 'Private documentation local links and referenced image files exist. No URLs were fetched.'
