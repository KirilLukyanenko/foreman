<#
.SYNOPSIS
  Builds the bundle for a Linux server: Foreman self-contained for linux-x64, the install script and the systemd unit.

.DESCRIPTION
  The server needs no .NET: the bundle carries its own runtime. Copy it to the server and run install.sh there:

    scp artifacts/foreman-linux-x64.tar.gz user@server:
    ssh user@server 'tar -xzf foreman-linux-x64.tar.gz && sudo bash foreman-linux-x64/install.sh'

  The same steps upgrade a server that has Foreman already: install.sh replaces /opt/foreman and keeps the jobs,
  the settings in /etc/foreman/foreman.env and Claude Code's sign-in.

.EXAMPLE
  .\deploy\publish.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'artifacts'
$bundle = Join-Path $out 'foreman-linux-x64'
Remove-Item -Recurse -Force $bundle -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $bundle | Out-Null

dotnet publish (Join-Path $root 'src/Foreman.Cli') -c Release -r linux-x64 --self-contained -o (Join-Path $bundle 'bin') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Copy-Item (Join-Path $PSScriptRoot 'install.sh'), (Join-Path $PSScriptRoot 'foreman.service'), (Join-Path $PSScriptRoot 'foreman-idle.sh'), (Join-Path $PSScriptRoot 'foreman-idle.service'), (Join-Path $PSScriptRoot 'foreman-idle.timer') $bundle
# Line endings: bash and systemd read LF only. The tar is Windows' own: Git's GNU tar reads "C:" as a remote host.
foreach ($f in 'install.sh', 'foreman.service', 'foreman-idle.sh', 'foreman-idle.service', 'foreman-idle.timer') {
    $path = Join-Path $bundle $f
    [IO.File]::WriteAllText($path, ([IO.File]::ReadAllText($path) -replace "`r`n", "`n"))
}

$tar = Join-Path $out 'foreman-linux-x64.tar.gz'
Remove-Item -Force $tar -ErrorAction SilentlyContinue
& (Join-Path $env:SystemRoot 'System32\tar.exe') -czf $tar -C $out 'foreman-linux-x64'
if ($LASTEXITCODE -ne 0) { throw "tar failed" }
$size = [math]::Round((Get-Item $tar).Length / 1MB)
Write-Host "  $tar ($size MB)"
Write-Host '  Next: scp it to the server, then: tar -xzf foreman-linux-x64.tar.gz && sudo bash foreman-linux-x64/install.sh'
