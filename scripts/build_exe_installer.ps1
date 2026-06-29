param(
    [string]$OutputDir = "out",
    [string]$AppName = "ROS-APP",
    [string]$Version = "",
    [string]$Channel = "beta",
    [string]$UpdateBaseUrl = ""
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$channelName = if ([string]::IsNullOrWhiteSpace($Channel)) { "beta" } else { $Channel.Trim().ToLowerInvariant() }
if ($channelName -notin @("beta", "prod")) {
    throw "Invalid Channel: $Channel. Only beta/prod are supported."
}

function Get-DefaultUpdateBaseUrl([string]$channel) {
    if ($channel -eq "prod") {
        return "http://ros.yuban.cloud/ros/"
    }
    return "http://ros.yuban.cloud/ros/beta/"
}

function Normalize-BaseUrl([string]$url) {
    if ([string]::IsNullOrWhiteSpace($url)) {
        return ""
    }
    $trimmed = $url.Trim()
    if (-not $trimmed.EndsWith("/")) {
        $trimmed += "/"
    }
    return $trimmed
}

if ([string]::IsNullOrWhiteSpace($UpdateBaseUrl)) {
    $UpdateBaseUrl = Get-DefaultUpdateBaseUrl $channelName
}

$distRoot = Join-Path $root ("dist\" + $channelName)
$outDir = Join-Path $root (Join-Path $OutputDir $channelName)
$installerPath = ""
$installerLatestPath = Join-Path $outDir "$AppName-Setup.exe"
$iconPath = Join-Path $root "icons\app.ico"
$payloadZip = Join-Path $root "installer\payload.zip"
$installerProj = Join-Path $root "installer\ROSAppInstaller.csproj"
$publishDir = Join-Path $root "installer\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"
$changelog = Join-Path $root ("CHANGELOG." + $channelName + ".txt")
$utf8Bom = [System.Text.UTF8Encoding]::new($true)

function Get-NotesForVersion([string]$path, [string]$version) {
    if (-not (Test-Path $path)) {
        return ""
    }

    $lines = [System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)
    $pattern = '^\[(?<ts>[^\]]+)\]\s+版本\s+(?<ver>\S+)\s+-\s+(?<status>成功|失败)\s+-\s+(?<msg>.+?)(\s+-\s+环境:\s*(?<env>\S+))?(\s+-\s+渠道:\s*(?<channel>\S+))?\s*$'
    for ($i = $lines.Length - 1; $i -ge 0; $i--) {
        $line = $lines[$i].Trim()
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line -match $pattern -and $matches["ver"].Trim() -eq $version.Trim()) {
            return $matches["msg"].Trim()
        }
    }

    return ""
}

if (-not (Test-Path $distRoot)) {
    throw "dist channel folder not found: $distRoot"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $latest = Get-ChildItem -Path $distRoot -Directory |
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
        throw "No $AppName build folder found in $distRoot"
    }

    $sourceDir = $latest.Dir.FullName
    $version = $latest.Ver.ToString()
}
else {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') {
        throw "Invalid version format: $Version. Expected x.y.z"
    }
    $version = $Version
    $sourceDir = Join-Path $distRoot "${AppName}_$version"
    if (-not (Test-Path $sourceDir)) {
        throw "Target build folder not found: $sourceDir"
    }
}

$installerPath = Join-Path $outDir "$AppName-Setup-$version.exe"

if (Test-Path $outDir) {
    Remove-Item -Recurse -Force $outDir
}
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

if (-not (Test-Path $iconPath)) {
    throw "Icon not found: $iconPath"
}

Compress-Archive -Path (Join-Path $sourceDir "*") -DestinationPath $payloadZip -Force

dotnet publish $installerProj -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$version | Out-Host

$publishedExe = Join-Path $publishDir "ROSAppSetup.exe"
if (-not (Test-Path $publishedExe)) {
    throw "Installer build failed: $publishedExe not found."
}

Copy-Item -Force $publishedExe $installerPath
Copy-Item -Force $publishedExe $installerLatestPath

Remove-Item -Force $payloadZip

$baseUrl = Normalize-BaseUrl $UpdateBaseUrl
$downloadUrl = if ([string]::IsNullOrWhiteSpace($baseUrl)) { "" } else { "$baseUrl$AppName-Setup-$version.exe" }
$sha256 = (Get-FileHash $installerPath -Algorithm SHA256).Hash
$buildTime = (Get-Item $installerPath).LastWriteTime.ToString("yyyy-MM-ddTHH:mm:ssK")
$notes = Get-NotesForVersion $changelog $version

$latestJson = [ordered]@{
    version = $version
    downloadUrl = $downloadUrl
    notes = $notes
    sha256 = $sha256
    buildTime = $buildTime
}
$latestJsonPath = Join-Path $outDir "latest.json"
[System.IO.File]::WriteAllText($latestJsonPath, ($latestJson | ConvertTo-Json -Depth 3) + "`r`n", $utf8Bom)

Write-Host "Channel: $channelName"
Write-Host "Installer EXE: $installerPath"
Write-Host "Latest Alias: $installerLatestPath"
Write-Host "Latest JSON: $latestJsonPath"
