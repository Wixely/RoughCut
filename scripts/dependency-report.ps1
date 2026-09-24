# Prints every managed package the solution actually resolves, with its licence, from the committed
# lock files and the local NuGet cache. Regenerate the inventory in docs/dependencies.md from this,
# and run it after any dependency change: a licence that is not permissive is reported as a finding.
param([switch]$Detailed)
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    $cache = Join-Path $env:USERPROFILE '.nuget\packages'
    $locks = Get-ChildItem -Recurse -Filter packages.lock.json |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|artifacts)\\' }
    if ($locks.Count -eq 0) { throw 'No lock files found; run dotnet restore first.' }

    $packages = @{}
    foreach ($lock in $locks) {
        $document = Get-Content $lock.FullName -Raw | ConvertFrom-Json
        foreach ($framework in $document.dependencies.PSObject.Properties) {
            foreach ($entry in $framework.Value.PSObject.Properties) {
                if ($entry.Value.type -eq 'Project') { continue }
                $version = $entry.Value.resolved
                if (-not $version) { continue }
                $packages["$($entry.Name)|$version"] = $entry.Value.type
            }
        }
    }

    $rows = foreach ($key in $packages.Keys) {
        $name, $version = $key -split '\|', 2
        $nuspec = Join-Path $cache "$($name.ToLowerInvariant())\$version\$($name.ToLowerInvariant()).nuspec"
        $licence = 'unknown (package not in the local cache)'
        $project = ''
        if (Test-Path $nuspec) {
            $meta = ([xml](Get-Content $nuspec -Raw)).package.metadata
            $project = $meta.projectUrl
            if ($meta.license -and $meta.license.type -eq 'expression') { $licence = $meta.license.'#text' }
            elseif ($meta.license) {
                # A packaged licence file, not an expression: read it so the report names a licence
                # rather than a filename, and say where that reading came from.
                $file = Join-Path $cache "$($name.ToLowerInvariant())\$version\$($meta.license.'#text')"
                $licence = "file: $($meta.license.'#text')"
                if (Test-Path $file) {
                    $text = (Get-Content $file -Raw -ErrorAction SilentlyContinue)
                    if ($text -match '(?im)^\s*(The )?MIT License') { $licence = 'MIT (licence file)' }
                    elseif ($text -match '(?im)^\s*Apache License') { $licence = 'Apache-2.0 (licence file)' }
                }
            }
            elseif ($meta.licenseUrl) { $licence = "url: $($meta.licenseUrl)" }
        }
        [pscustomobject]@{
            Package = $name; Version = $version; Kind = $packages[$key]; Licence = $licence; Project = $project
        }
    }

    $rows = $rows | Sort-Object Package, Version
    if ($Detailed) { $rows | Format-Table -AutoSize }
    "$($rows.Count) resolved packages across $($locks.Count) projects"
    $rows | Group-Object Licence | Sort-Object Count -Descending |
        ForEach-Object { "{0,4}  {1}" -f $_.Count, $_.Name }

    # Everything here has been permissive so far. Anything else is a decision, not a detail.
    $permissive = @('MIT', 'MIT (licence file)', 'Apache-2.0', 'Apache-2.0 (licence file)',
        'Zlib', 'BSD-3-Clause', 'BSD-2-Clause', 'ISC')
    $review = $rows | Where-Object { $permissive -notcontains $_.Licence }
    if ($review) {
        ''
        'Needs review:'
        $review | Format-Table Package, Version, Licence -AutoSize
        exit 1
    }
    ''
    'All resolved packages carry a permissive licence.'
}
finally { Pop-Location }
