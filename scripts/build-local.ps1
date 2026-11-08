<#
.SYNOPSIS
Builds RimMCP for a local Windows install: the mod DLL, the MCP proxy, and optionally the
regression suite and a local copy of the mod in RimWorld's Mods folder.

.DESCRIPTION
Installs only what is missing, without admin rights and without changing PATH or the registry
outside this process:
  - .NET 8 SDK into %LOCALAPPDATA%\Microsoft\dotnet (dotnet-install.ps1)
  - Rust via rustup into %USERPROFILE%\.cargo and .rustup, with the toolchain pinned in
    rust-toolchain.toml

Then builds RiMCP/Assemblies/RiMCP.dll against RimWorld 1.6's managed assemblies, builds the
proxy for this machine, and copies it to RiMCP/Tools/McpProxy/win-x64/RiMCP.McpProxy.exe.

.PARAMETER RimWorldManagedPath
RimWorld 1.6's managed-assembly directory, used as build references.

.PARAMETER RimWorldPath
RimWorld's install directory, for -Install. Defaults to two levels above RimWorldManagedPath.

.PARAMETER RimWorldConfigPath
RimWorld's Config directory, where mod settings live, for -Install. When the local copy has no
settings yet, the Workshop copy's settings file is copied for it, and with it the bridge token
and access level: keep the copy as private as the original.

.PARAMETER Test
Also build and run the compatibility regression suite.

.PARAMETER Install
Replace <RimWorldPath>\Mods\RiMCP with the built mod: it is staged next to the target and swapped
in, so a failure leaves the previous copy in place. Refused while RimWorld or a program from the
target (such as its MCP proxy, run by an MCP client) is running, and when the target is a link
or inside this repository.

.PARAMETER Force
With -Install: install even while RimWorld is running. Programs running from the target still
stop it, since their files cannot be replaced.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-local.ps1 -Test
#>
[CmdletBinding()]
param(
    [string]$RimWorldManagedPath = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed',
    [string]$RimWorldPath,
    [string]$RimWorldConfigPath = (Join-Path $env:USERPROFILE 'AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config'),
    [switch]$Test,
    [switch]$Install,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Root = Split-Path -Parent $PSScriptRoot
$Scratch = Join-Path $env:TEMP 'rimcp-build-local'
$DotnetRoot = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
$Dotnet = Join-Path $DotnetRoot 'dotnet.exe'
$CargoBin = Join-Path $env:USERPROFILE '.cargo\bin'
$Dll = Join-Path $Root 'RiMCP\Assemblies\RiMCP.dll'
$Proxy = Join-Path $Root 'RiMCP\Tools\McpProxy\win-x64\RiMCP.McpProxy.exe'

function Write-Step([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# Runs a native command, echoing its output. Its exit code decides failure: Windows PowerShell
# turns a native command's stderr (cargo's progress, rustup's notes) into errors when its own
# output is redirected, which must not stop the script.
function Invoke-Native([string]$Command, [string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    & $Command @Arguments 2>&1 | ForEach-Object { if ($_ -is [Management.Automation.ErrorRecord]) { $_.Exception.Message } else { $_ } } | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "$([IO.Path]::GetFileName($Command)) $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

# A native command's standard output, ignoring its stderr and exit code.
function Read-Native([string]$Command, [string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    return & $Command @Arguments 2>$null
}

function Get-Download([string]$Url, [string]$Name) {
    New-Item -ItemType Directory -Force -Path $Scratch | Out-Null
    $path = Join-Path $Scratch $Name
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $path
    return $path
}

function Test-DotnetSdk8 {
    if (-not (Test-Path $Dotnet)) {
        return $false
    }
    $sdks = Read-Native $Dotnet @('--list-sdks')
    return [bool]($sdks | Where-Object { $_ -match '^8\.' })
}

function Initialize-Dotnet {
    if (-not (Test-DotnetSdk8)) {
        Write-Step "Installing the .NET 8 SDK into $DotnetRoot"
        $installer = Get-Download 'https://dot.net/v1/dotnet-install.ps1' 'dotnet-install.ps1'
        Invoke-Native 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $installer, '-Channel', '8.0', '-InstallDir', $DotnetRoot, '-NoPath')
        if (-not (Test-DotnetSdk8)) {
            throw ".NET 8 SDK is still missing from $DotnetRoot after installing it."
        }
    }
    # This process only: the x86 runtime-only host on the machine PATH must not answer 'dotnet',
    # which the proxy's build script calls by name.
    $env:DOTNET_ROOT = $DotnetRoot
    $env:PATH = "$DotnetRoot;$env:PATH"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
}

function Initialize-Rust {
    $rustup = Join-Path $CargoBin 'rustup.exe'
    if (-not (Test-Path $rustup)) {
        Write-Step 'Installing rustup'
        $installer = Get-Download 'https://win.rustup.rs/x86_64' 'rustup-init.exe'
        Invoke-Native $installer @('-y', '--default-toolchain', 'none', '--profile', 'minimal', '--no-modify-path')
    }
    $env:PATH = "$CargoBin;$env:PATH"
    # Git for Windows ships a coreutils link.exe that would shadow the MSVC linker.
    $env:PATH = (($env:PATH -split ';') | Where-Object {
        $_ -and -not ((Test-Path (Join-Path $_ 'link.exe')) -and -not (Test-Path (Join-Path $_ 'cl.exe')))
    }) -join ';'
    $channel = (Select-String -Path (Join-Path $Root 'rust-toolchain.toml') -Pattern '^\s*channel\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
    $installed = Read-Native $rustup @('toolchain', 'list')
    if (-not ($installed | Where-Object { $_ -like "$channel-*" })) {
        Write-Step "Installing Rust $channel"
        Invoke-Native $rustup @('toolchain', 'install', $channel, '--profile', 'minimal', '--component', 'rustfmt', '--component', 'clippy')
    }
}

function Build-Mod {
    Write-Step 'Building RiMCP.dll'
    Invoke-Native $Dotnet @('build', (Join-Path $Root 'RiMCP\Source\RiMCP.csproj'), '-c', 'Release', '-nologo', "-p:RimWorldManagedPath=$RimWorldManagedPath")
    if (-not (Test-Path $Dll)) {
        throw "The build did not produce $Dll."
    }
}

function Build-Proxy {
    Write-Step 'Building the MCP proxy'
    Push-Location $Root
    try {
        Invoke-Native (Join-Path $CargoBin 'cargo.exe') @('build', '--locked', '--release', '--manifest-path', 'McpProxy\Cargo.toml')
    }
    finally {
        Pop-Location
    }
    $binary = Join-Path $Root 'McpProxy\target\release\rimcp-proxy.exe'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Proxy) | Out-Null
    Copy-Item -Force $binary $Proxy
    Copy-Item -Force (Join-Path $Root 'LICENSE') (Join-Path (Split-Path -Parent $Proxy) 'LICENSE.TXT')
}

# The manifest the proxy embedded, as its build script exported it.
function Get-ManifestToolCount {
    $manifest = Get-ChildItem -Path (Join-Path $Root 'McpProxy\target\release\build') -Filter 'manifest.json' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'out' -and $_.Directory.Parent.Name -like 'rimcp-proxy-*' } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $manifest) {
        return $null
    }
    return @((Get-Content -Raw $manifest.FullName | ConvertFrom-Json).tools).Count
}

function Invoke-RegressionSuite {
    Write-Step 'Building and running the compatibility regression suite'
    Invoke-Native $Dotnet @('build', (Join-Path $Root 'tests\CompatibilityRegression\CompatibilityRegression.csproj'), '-c', 'Release', '-nologo', "-p:RimWorldManagedPath=$RimWorldManagedPath")
    $runner = Join-Path $Root 'tests\CompatibilityRegression\bin\Release\net472\CompatibilityRegression.exe'
    $ErrorActionPreference = 'Continue'
    $output = & $runner $RimWorldManagedPath $Dll 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $output | Where-Object { $_ -notmatch '^PASS ' } | ForEach-Object { Write-Host $_ }
    if ($code -ne 0) {
        throw "The regression suite failed with exit code $code."
    }
    return ($output | Select-Object -Last 1)
}

function Test-ReparsePoint([string]$Path) {
    return (Test-Path $Path) -and (([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::ReparsePoint) -ne 0)
}

# The first link (junction or symbolic link) anywhere under Path, without following any, or null.
function Find-ReparsePoint([string]$Path) {
    # An array, not a lazy enumeration, which would keep the folder open after an early return.
    foreach ($entry in [IO.Directory]::GetFileSystemEntries($Path)) {
        $attributes = [IO.File]::GetAttributes($entry)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            return $entry
        }
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            $found = Find-ReparsePoint $entry
            if ($found) {
                return $found
            }
        }
    }
    return $null
}

# Deletes a folder without following links inside it: rmdir removes a junction, not its target.
function Remove-Folder([string]$Path) {
    if (Test-Path $Path) {
        cmd /c rmdir /s /q "$Path" | Out-Null
        if (Test-Path $Path) {
            throw "Could not delete $Path."
        }
    }
}

function Install-Mod {
    $running = Get-Process -Name 'RimWorldWin64' -ErrorAction SilentlyContinue
    if ($running -and -not $Force) {
        throw 'RimWorld is running. Close it before -Install, or pass -Force.'
    }
    $game = if ($RimWorldPath) { $RimWorldPath } else { Split-Path -Parent (Split-Path -Parent $RimWorldManagedPath) }
    $mods = [IO.Path]::GetFullPath((Join-Path $game 'Mods'))
    if (-not (Test-Path $mods)) {
        throw "No Mods folder in $game; pass -RimWorldPath."
    }
    $target = Join-Path $mods 'RiMCP'
    $repo = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (($target + '\').StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The install target $target is inside this repository."
    }
    if ((Test-ReparsePoint $mods) -or (Test-ReparsePoint $target)) {
        throw "The install target $target is a link or inside one; replace it by hand."
    }
    $link = if (Test-Path $target) { Find-ReparsePoint $target } else { $null }
    if ($link) {
        throw "The install target holds a link, $link; replace the target by hand."
    }
    # A running program from the target, such as the MCP proxy an MCP client started, keeps its
    # files locked.
    $prefix = $target + '\'
    $users = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
    if ($users.Count -gt 0) {
        $names = ($users | ForEach-Object { "$($_.Name) ($($_.ProcessId))" }) -join ', '
        throw "Programs from $target are running: $names. Close them (for the MCP proxy, the MCP client that started it) before -Install."
    }

    Write-Step "Installing the mod to $target"
    $suffix = [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $staging = Join-Path $mods ".RiMCP.staging-$suffix"
    $previous = Join-Path $mods ".RiMCP.previous-$suffix"
    try {
        New-Item -ItemType Directory -Path $staging | Out-Null
        foreach ($folder in 'About', 'Assemblies', 'Defs', 'Textures', 'Tools') {
            Copy-Item -Recurse (Join-Path $Root "RiMCP\$folder") (Join-Path $staging $folder)
        }
        # The Workshop item's id belongs to the published mod, not this local copy.
        Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $staging 'About\PublishedFileId.txt')
        Get-ChildItem -Path (Join-Path $staging 'Assemblies') -Filter '*.pdb' | Remove-Item -Force
        # Two renames on one volume: the previous copy stays whole until the new one is in place.
        if (Test-Path $target) {
            [IO.Directory]::Move($target, $previous)
        }
        try {
            [IO.Directory]::Move($staging, $target)
        }
        catch {
            if (Test-Path $previous) {
                [IO.Directory]::Move($previous, $target)
            }
            throw
        }
    }
    finally {
        Remove-Folder $staging
    }
    if (Test-Path $previous) {
        try {
            Remove-Folder $previous
        }
        catch {
            Write-Warning "The new copy is installed, but the previous one could not be deleted: $previous. Delete it before starting RimWorld, which would list it as a second copy."
        }
    }

    # Mod settings are keyed by mod folder name, so the local copy starts with the Workshop copy's
    # token and access instead of new ones.
    $settings = Join-Path $RimWorldConfigPath 'Mod_RiMCP_RiMCPMod.xml'
    $workshop = Join-Path $RimWorldConfigPath 'Mod_3809698070_RiMCPMod.xml'
    if (-not (Test-Path $settings) -and (Test-Path $workshop)) {
        Copy-Item $workshop $settings
        Write-Host "Copied the Workshop copy's settings, with its token, to $settings"
    }
    return $target
}

Write-Step "Repository: $Root"
if (-not (Test-Path (Join-Path $RimWorldManagedPath 'Assembly-CSharp.dll'))) {
    throw "No Assembly-CSharp.dll in $RimWorldManagedPath; pass -RimWorldManagedPath."
}
Initialize-Dotnet
Initialize-Rust
Build-Mod
Build-Proxy
$tests = if ($Test) { Invoke-RegressionSuite } else { $null }
$installed = if ($Install) { Install-Mod } else { $null }

Write-Host ''
Write-Host 'Summary' -ForegroundColor Green
Write-Host "  Mod DLL:    $Dll"
Write-Host "  Proxy:      $Proxy"
Write-Host "  MCP tools:  $(Get-ManifestToolCount)"
if ($tests) {
    Write-Host "  Regression: $tests"
}
if ($installed) {
    Write-Host "  Installed:  $installed"
}
