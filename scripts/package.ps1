# Produces the two things people install: the MCP server an agent host launches, and the desktop window a
# person opens. Both come from the same shared library, so they are published together and stamped with the
# same version — an agent and a person editing the same project must agree about what a project means.
#
#   .\scripts\package.ps1                 both, framework-dependent (needs .NET 10 on the machine)
#   .\scripts\package.ps1 -SelfContained  both, carrying their own runtime (nothing to install)
#   .\scripts\package.ps1 -Only mcp       just the server
param(
    [ValidateSet('both', 'mcp', 'desktop')][string]$Only = 'both',
    [switch]$SelfContained,
    [string]$Runtime = 'win-x64',
    [string]$Version
)
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    if (-not $Version) {
        # Untagged builds are named after the commit they came from, so an artefact can always be traced back.
        $describe = & git describe --tags --always --dirty 2>$null
        $Version = if ($LASTEXITCODE -eq 0 -and $describe) { $describe } else { '0.0.0-local' }
    }
    $destination = Join-Path 'artifacts/package' $Version
    if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }

    # Locked restore, because a package that drifted is a different product from the one that was verified.
    & dotnet restore RoughCut.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed; a dependency changed without its lock file.' }

    $targets = @()
    if ($Only -in @('both', 'mcp')) { $targets += @{ Project = 'src/RoughCut.Mcp'; Name = 'roughcut-mcp' } }
    if ($Only -in @('both', 'desktop')) { $targets += @{ Project = 'src/RoughCut.Desktop'; Name = 'roughcut-desktop' } }

    foreach ($target in $targets) {
        $output = Join-Path $destination $target.Name
        $arguments = @($target.Project, '-c', 'Release', '-r', $Runtime, '-o', $output)
        # NativeAOT is deliberately not used for either: the MCP host builds its tool schema by reflection and
        # loads native speech runtimes, and the desktop shell loads native decoders. The CLI is the AOT one.
        if ($SelfContained) { $arguments += @('--self-contained', 'true') }
        else { $arguments += @('--self-contained', 'false') }
        & dotnet publish @arguments
        if ($LASTEXITCODE -ne 0) { throw "Publishing $($target.Name) failed." }
    }

    # A checksum per artefact, so what was verified here can be recognised elsewhere.
    $manifest = Join-Path $destination 'checksums.txt'
    Get-ChildItem $destination -Recurse -File -Include '*.exe', '*.dll' |
        Where-Object { $_.Name -like 'roughcut*' } |
        ForEach-Object {
            $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $($_.FullName.Substring($destination.Length + 1))"
        } | Set-Content $manifest -Encoding utf8

    Write-Host ''
    Write-Host "RoughCut $Version packaged into $destination"
    foreach ($target in $targets) {
        $exe = Join-Path (Join-Path $destination $target.Name) "$($target.Name).exe"
        if (Test-Path $exe) {
            $size = [math]::Round((Get-ChildItem (Split-Path $exe) -Recurse -File |
                Measure-Object -Property Length -Sum).Sum / 1MB, 1)
            Write-Host ("  {0,-18} {1,6} MB  {2}" -f $target.Name, $size, $exe)
        }
    }
}
finally { Pop-Location }
