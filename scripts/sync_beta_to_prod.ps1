param(
    [string]$Version = "",
    [string]$AppName = "ROS-APP",
    [string]$ProdUpdateBaseUrl = "http://ros.yuban.cloud/ros/"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$betaDistRoot = Join-Path $root "dist\beta"
$prodDistRoot = Join-Path $root "dist\prod"
$betaChangelog = Join-Path $root "CHANGELOG.beta.txt"
$prodChangelog = Join-Path $root "CHANGELOG.prod.txt"
$legacyChangelog = Join-Path $root "CHANGELOG.txt"
$utf8Bom = [System.Text.UTF8Encoding]::new($true)

function Sync-BetaChangelogToProd([string]$sourcePath, [string]$fallbackPath, [string]$targetPath, [Version]$maxVersion) {
    if (-not (Test-Path $targetPath)) {
        New-Item -ItemType File -Path $targetPath -Force | Out-Null
    }

    $pattern = '^\[(?<ts>[^\]]+)\]\s+版本\s+(?<ver>\d+\.\d+\.\d+)\s+-\s+(?<status>成功|失败)\s+-\s+(?<msg>.+?)(\s+-\s+环境:\s*(?<env>\S+))?(\s+-\s+渠道:\s*(?<channel>\S+))?\s*$'
    $sourceLines = New-Object System.Collections.Generic.List[string]
    if ((Test-Path $sourcePath) -and (Get-Item $sourcePath).Length -gt 0) {
        [void]$sourceLines.AddRange([System.IO.File]::ReadAllLines($sourcePath, [System.Text.Encoding]::UTF8))
    }
    if (Test-Path $fallbackPath) {
        [void]$sourceLines.AddRange([System.IO.File]::ReadAllLines($fallbackPath, [System.Text.Encoding]::UTF8))
    }
    if (-not $sourceLines -or $sourceLines.Count -eq 0) {
        return
    }

    $existingLines = [System.IO.File]::ReadAllLines($targetPath, [System.Text.Encoding]::UTF8)
    $existingVersions = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($line in $existingLines) {
        if ($line -match $pattern) {
            $existingVersions.Add($matches["ver"].Trim()) | Out-Null
        }
    }

    $appendLines = New-Object System.Collections.Generic.List[string]
    foreach ($line in $sourceLines) {
        if (-not ($line -match $pattern)) {
            continue
        }
        $channelRaw = if ($matches.ContainsKey("channel")) { $matches["channel"] } else { "" }
        $envRaw = if ($matches.ContainsKey("env")) { $matches["env"] } else { "" }
        $channelText = if ($null -eq $channelRaw) { "" } else { $channelRaw.Trim().ToLowerInvariant() }
        $envText = if ($null -eq $envRaw) { "" } else { $envRaw.Trim().ToLowerInvariant() }
        if ($channelText -ne "beta" -and $envText -ne "beta") {
            continue
        }

        $versionText = $matches["ver"].Trim()
        $entryVersion = [Version]$versionText
        if ($entryVersion -gt $maxVersion) {
            continue
        }
        if ($existingVersions.Contains($versionText)) {
            continue
        }

        $appendLines.Add($line) | Out-Null
        $existingVersions.Add($versionText) | Out-Null
    }

    if ($appendLines.Count -gt 0) {
        $payload = ($appendLines -join "`r`n") + "`r`n"
        [System.IO.File]::AppendAllText($targetPath, $payload, $utf8Bom)
    }
}

if (-not (Test-Path $betaDistRoot)) {
    throw "Beta dist folder not found: $betaDistRoot"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $latest = Get-ChildItem -Path $betaDistRoot -Directory |
        Where-Object { $_.Name -like "${AppName}_*" } |
        ForEach-Object {
            $verString = $_.Name.Substring($AppName.Length + 1)
            if ($verString -match '^\d+\.\d+\.\d+$') {
                [PSCustomObject]@{
                    Dir = $_
                    Ver = [Version]$verString
                }
            }
        } |
        Where-Object { $_ -ne $null } |
        Sort-Object Ver -Descending |
        Select-Object -First 1

    if (-not $latest) {
        throw "No beta build found in $betaDistRoot"
    }

    $Version = $latest.Ver.ToString()
}

$betaDir = Join-Path $betaDistRoot "${AppName}_$Version"
if (-not (Test-Path $betaDir)) {
    throw "Beta build not found: $betaDir"
}

$targetVersion = [Version]$Version
Sync-BetaChangelogToProd -sourcePath $betaChangelog -fallbackPath $legacyChangelog -targetPath $prodChangelog -maxVersion $targetVersion

$prodDir = Join-Path $prodDistRoot "${AppName}_$Version"
if (Test-Path $prodDir) {
    Remove-Item -Recurse -Force $prodDir
}
New-Item -ItemType Directory -Force -Path $prodDir | Out-Null
Copy-Item -Force -Recurse (Join-Path $betaDir "*") $prodDir

powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build_exe_installer.ps1') -Channel prod -Version $Version -UpdateBaseUrl $ProdUpdateBaseUrl

Write-Host "Synced beta -> prod: $Version"
Write-Host "Prod dist: $prodDir"
Write-Host "Prod out : $(Join-Path $root 'out\prod')"
