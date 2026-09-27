param([switch]$PublishAot, [switch]$Published)
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    # Locked restore first: the committed lock files are what a build machine must resolve, or fail.
    & dotnet restore RoughCut.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed; a dependency changed without its lock file.' }
    & .\scripts\dependency-report.ps1
    if ($LASTEXITCODE -ne 0) { throw 'A resolved package licence needs review.' }
    & dotnet build RoughCut.slnx
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & dotnet run --project tests/RoughCut.Tests --no-build -- --media --cli src/RoughCut.Cli/bin/Debug/net10.0/roughcut.dll --desktop src/RoughCut.Desktop/bin/Debug/net10.0/roughcut-desktop.dll --mcp src/RoughCut.Mcp/bin/Debug/net10.0/roughcut-mcp.dll
    if ($LASTEXITCODE -ne 0) { throw 'Managed verification failed.' }
    if ($Published) {
        # Deployment is where native assets and resources stop resolving, so the suite runs against the
        # published executables as well as the build output: framework-dependent, plus a self-contained
        # desktop build, which is the artefact a person would actually be handed.
        $outputs = @{ 'RoughCut.Cli' = 'roughcut'; 'RoughCut.Desktop' = 'roughcut-desktop'; 'RoughCut.Mcp' = 'roughcut-mcp' }
        foreach ($project in $outputs.Keys) {
            & dotnet publish "src/$project" -c Release -o "artifacts/publish/$project"
            if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
        }
        & dotnet publish src/RoughCut.Desktop -c Release -r win-x64 --self-contained true -o artifacts/publish/desktop-selfcontained
        if ($LASTEXITCODE -ne 0) { throw 'Publishing the self-contained desktop failed.' }
        foreach ($desktop in @('artifacts/publish/RoughCut.Desktop/roughcut-desktop.exe',
            'artifacts/publish/desktop-selfcontained/roughcut-desktop.exe')) {
            & dotnet run --project tests/RoughCut.Tests --no-build -- --media --cli artifacts/publish/RoughCut.Cli/roughcut.exe --desktop $desktop --mcp artifacts/publish/RoughCut.Mcp/roughcut-mcp.exe
            if ($LASTEXITCODE -ne 0) { throw "Published verification failed against $desktop." }
        }
    }
    if ($PublishAot) {
        & dotnet publish src/RoughCut.Cli -c Release -r win-x64 -p:PublishAot=true -o artifacts/publish/win-x64
        if ($LASTEXITCODE -ne 0) { throw 'NativeAOT publish failed.' }
        & dotnet run --project tests/RoughCut.Tests --no-build -- --media --cli artifacts/publish/win-x64/roughcut.exe --desktop src/RoughCut.Desktop/bin/Debug/net10.0/roughcut-desktop.dll --mcp src/RoughCut.Mcp/bin/Debug/net10.0/roughcut-mcp.dll
        if ($LASTEXITCODE -ne 0) { throw 'NativeAOT CLI verification failed.' }
    }
}
finally { Pop-Location }
