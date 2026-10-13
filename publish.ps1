<#
Builds NoirMech from the AnoMech checkout and publishes it to this plugin repository.

  .\publish.ps1                       # build, update repo.json, commit and push
  .\publish.ps1 -Changelog "..."      # same, with a changelog line shown in the installer
  .\publish.ps1 -NoPush               # everything except the push

The version is the upstream <Version> from AnoMech.csproj with our own fourth segment, which goes
up by one on every publish, so Dalamud sees each publish as an update. Each build ships with a zip
of the exact source it was built from, which AnoMech's AGPL-3.0 licence requires.
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\Anomech'),
    [string]$Branch = 'noirmech-stable',
    [string]$Changelog = '',
    [switch]$NoPush
)

$ErrorActionPreference = 'Stop'
$InternalName = 'NoirMech'
$RawBase = 'https://raw.githubusercontent.com/NoirYamato/DalamudPlugins/main'
$PluginDir = Join-Path $PSScriptRoot "plugins\$InternalName"
$RepoJson = Join-Path $PSScriptRoot 'repo.json'

function Find-Tool([string]$name, [string]$fallback) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    if (Test-Path $fallback) { return $fallback }
    throw "$name not found."
}
$git = Find-Tool 'git' 'C:\Program Files\Git\cmd\git.exe'
$dotnet = Find-Tool 'dotnet' 'C:\Program Files\dotnet\dotnet.exe'

function Invoke-Checked([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

function Write-Utf8([string]$path, [string]$text) {
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false))
}

$Source = (Resolve-Path $Source).Path

$currentBranch = (& $git -C $Source rev-parse --abbrev-ref HEAD).Trim()
if ($currentBranch -ne $Branch) { throw "$Source is on '$currentBranch'; switch it to '$Branch' first." }
if (& $git -C $Source status --porcelain) { throw "$Source has uncommitted changes; commit or stash them first." }
$commit = (& $git -C $Source rev-parse HEAD).Trim()

$csproj = Join-Path $Source 'AnoMech\AnoMech.csproj'
$upstream = (([xml](Get-Content -Raw $csproj)).Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version.Trim()
$parts = @($upstream.Split('.')) + @('0', '0', '0')
$base = '{0}.{1}.{2}' -f $parts[0], $parts[1], $parts[2]

$revision = 1
if (Test-Path $RepoJson) {
    $previous = @(Get-Content -Raw $RepoJson | ConvertFrom-Json) | Where-Object { $_.InternalName -eq $InternalName }
    if ($previous -and $previous.AssemblyVersion.StartsWith("$base.")) {
        $revision = [int]$previous.AssemblyVersion.Split('.')[3] + 1
    }
}
$version = "$base.$revision"
Write-Host "Publishing $InternalName $version from $Branch @ $($commit.Substring(0, 7))"

$releaseOut = Join-Path $Source 'AnoMech\bin\Release'
if (Test-Path $releaseOut) { Remove-Item -Recurse -Force $releaseOut }
Invoke-Checked $dotnet @('build', $csproj, '-c', 'Release', "-p:Version=$version", '--no-incremental', '-nologo', '-v:minimal')

$packaged = Join-Path $releaseOut $InternalName
$zip = Join-Path $packaged 'latest.zip'
$manifestPath = Join-Path $packaged "$InternalName.json"
if (-not (Test-Path $zip) -or -not (Test-Path $manifestPath)) { throw "Packager output not found in $packaged." }
$manifest = Get-Content -Raw $manifestPath | ConvertFrom-Json
if ($manifest.InternalName -ne $InternalName -or $manifest.AssemblyVersion -ne $version) {
    throw "Built manifest is $($manifest.InternalName) $($manifest.AssemblyVersion), expected $InternalName $version."
}

New-Item -ItemType Directory -Force $PluginDir | Out-Null
$zipName = "$InternalName-$version.zip"
$sourceName = "$InternalName-$version-source.zip"
Copy-Item $zip (Join-Path $PluginDir $zipName) -Force
Copy-Item $manifestPath (Join-Path $PluginDir "$InternalName.json") -Force
Invoke-Checked $git @('-C', $Source, 'archive', '--format=zip', "--prefix=$InternalName-$version-source/", '-o', (Join-Path $PluginDir $sourceName), $commit)

# Keep the previous build next to the new one: raw.githubusercontent.com can serve a cached
# repo.json for a few minutes, and its download link must still resolve.
foreach ($pattern in @("$InternalName-*-source.zip", "$InternalName-*.zip")) {
    Get-ChildItem $PluginDir -Filter $pattern |
        Where-Object { ($pattern -like '*-source.zip') -eq ($_.Name -like '*-source.zip') } |
        Sort-Object { [version]($_.Name -replace "^$InternalName-", '' -replace '(-source)?\.zip$', '') } -Descending |
        Select-Object -Skip 2 |
        Remove-Item -Force
}

$download = "$RawBase/plugins/$InternalName/$zipName"
$entry = [ordered]@{
    Author              = $manifest.Author
    Name                = $manifest.Name
    Punchline           = $manifest.Punchline
    Description         = $manifest.Description
    InternalName        = $manifest.InternalName
    AssemblyVersion     = $manifest.AssemblyVersion
    RepoUrl             = $manifest.RepoUrl
    ApplicableVersion   = $manifest.ApplicableVersion
    Tags                = @($manifest.Tags)
    DalamudApiLevel     = $manifest.DalamudApiLevel
    IconUrl             = $manifest.IconUrl
    DownloadLinkInstall = $download
    DownloadLinkTesting = $download
    DownloadLinkUpdate  = $download
    IsHide              = $false
    IsTestingExclusive  = $false
    DownloadCount       = 0
    LastUpdate          = [string][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
}
if ($Changelog) { $entry.Changelog = $Changelog }
Write-Utf8 $RepoJson ((ConvertTo-Json -InputObject @([pscustomobject]$entry) -Depth 5) + "`n")

Invoke-Checked $git @('-C', $PSScriptRoot, 'add', '-A')
Invoke-Checked $git @('-C', $PSScriptRoot, 'commit', '-q', '-m', "Publish $InternalName $version", '-m', "Built from $Branch @ $commit")
if (-not $NoPush) { Invoke-Checked $git @('-C', $PSScriptRoot, 'push', 'origin', 'main') }
Write-Host "Done: $InternalName $version$(if ($NoPush) { ' (not pushed)' })"
