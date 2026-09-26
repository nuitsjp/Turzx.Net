[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Version,
    [switch]$Preview
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ExitCode([string]$Operation) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed (exit code $LASTEXITCODE)."
    }
}

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
Push-Location -LiteralPath $projectRoot
try {
    $versionPattern = '^v(?<number>\d+\.\d+\.\d+)$'
    $localTags = @(& git tag --list 'v*')
    Assert-ExitCode 'git tag --list'
    $remoteRefs = @(& git ls-remote --tags origin 'refs/tags/v*')
    Assert-ExitCode 'git ls-remote'
    $remoteTags = @(
        foreach ($entry in $remoteRefs) {
            if ($entry -match 'refs/tags/(?<name>v\d+\.\d+\.\d+)$') { $Matches['name'] }
        }
    )
    $tags = @($localTags) + @($remoteTags)
    $releasedVersions = @(
        foreach ($name in $tags) {
            if ($name -match $versionPattern) {
                [pscustomobject]@{ Name = $name; Number = [version]$Matches['number'] }
            }
        }
    )
    $latest = $releasedVersions | Sort-Object -Property Number -Descending | Select-Object -First 1

    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        $number = $Version.TrimStart('v')
        if ($number -notmatch '^\d+\.\d+\.\d+$') {
            throw 'Version must be MAJOR.MINOR.PATCH (optionally prefixed with v).'
        }
        $releaseVersion = [version]$number
    }
    elseif ($null -ne $latest) {
        $releaseVersion = [version]::new($latest.Number.Major, $latest.Number.Minor, $latest.Number.Build + 1)
    }
    else {
        [xml]$project = Get-Content -LiteralPath 'src/Turzx.Net/Turzx.Net.csproj' -Raw
        $baseline = [version]$project.Project.PropertyGroup.Version
        $releaseVersion = [version]::new($baseline.Major, $baseline.Minor, $baseline.Build + 1)
    }

    $tag = "v$releaseVersion"
    Write-Host "Release version: $releaseVersion (tag $tag)"
    if ($Preview) { return }

    $status = @(& git status --porcelain=v1 --untracked-files=all)
    Assert-ExitCode 'git status'
    if ($status.Count -gt 0) {
        throw 'Working tree is not clean. Commit all release sources before tagging.'
    }
    if ([string]::IsNullOrWhiteSpace($env:NUGET_API_KEY)) {
        throw 'NUGET_API_KEY is required to publish the package.'
    }
    if ($tags -contains $tag) {
        throw "Tag $tag already exists locally or on origin."
    }
    if ($null -ne $latest -and $releaseVersion -le $latest.Number) {
        throw "Version must be greater than the latest local release tag $($latest.Name)."
    }

    & dotnet test 'tests/Turzx.Net.Tests/Turzx.Net.Tests.csproj' -c Release
    Assert-ExitCode 'dotnet test'

    & dotnet pack 'src/Turzx.Net/Turzx.Net.csproj' -c Release "-p:Version=$releaseVersion" -o 'artifacts/packages'
    Assert-ExitCode 'dotnet pack'
    $package = Join-Path $projectRoot "artifacts/packages/Turzx.Net.$releaseVersion.nupkg"
    if (-not (Test-Path -LiteralPath $package -PathType Leaf)) {
        throw "Expected package was not created: $package"
    }

    & git tag -a $tag -m "Release $tag"
    Assert-ExitCode 'git tag'
    & git push origin $tag
    Assert-ExitCode 'git push'

    & dotnet nuget push $package --source 'https://api.nuget.org/v3/index.json' --api-key $env:NUGET_API_KEY
    Assert-ExitCode 'dotnet nuget push'
    Write-Host "Published Turzx.Net $releaseVersion from $tag."
}
finally {
    Pop-Location
}
