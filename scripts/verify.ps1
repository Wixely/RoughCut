param([switch]$PublishAot)
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    & dotnet build RoughCut.slnx
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & dotnet run --project tests/RoughCut.Tests --no-build -- --media --cli src/RoughCut.Cli/bin/Debug/net10.0/roughcut.dll --mcp src/RoughCut.Mcp/bin/Debug/net10.0/roughcut-mcp.dll
    if ($LASTEXITCODE -ne 0) { throw 'Managed verification failed.' }
    if ($PublishAot) {
        & dotnet publish src/RoughCut.Cli -c Release -r win-x64 -p:PublishAot=true -o artifacts/publish/win-x64
        if ($LASTEXITCODE -ne 0) { throw 'NativeAOT publish failed.' }
        & dotnet run --project tests/RoughCut.Tests --no-build -- --media --cli artifacts/publish/win-x64/roughcut.exe --mcp src/RoughCut.Mcp/bin/Debug/net10.0/roughcut-mcp.dll
        if ($LASTEXITCODE -ne 0) { throw 'NativeAOT CLI verification failed.' }
    }
}
finally { Pop-Location }
