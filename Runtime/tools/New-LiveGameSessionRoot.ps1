#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string] $Path)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($Path) -or (Test-Path -LiteralPath $Path)) {
    throw 'Supply a NEW absolute directory inside isolated session artifacts, never an existing project/data directory.'
}
$parent = [IO.Directory]::GetParent($Path)
if (-not $parent.Exists -or $parent.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
    throw 'Parent must be an existing non-linked isolated artifacts directory.'
}
if (-not $IsWindows) { throw 'This provisioning helper currently supports Windows private ACLs only. Provision mode 0700 manually on Unix.' }
# Use the existing OS logon identity; do not generate or read a credential.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
$security = [Security.AccessControl.DirectorySecurity]::new()
$security.SetAccessRuleProtection($true, $false)
$security.SetOwner($identity)
$rule = [Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl',
    'ContainerInherit,ObjectInherit', 'None', 'Allow')
$security.AddAccessRule($rule)
$directory = [IO.DirectoryInfo]::new($Path)
[IO.FileSystemAclExtensions]::Create($directory, $security)
$directory.FullName
