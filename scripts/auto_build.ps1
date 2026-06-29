param(
    [string]$Message = "",
    [string]$Environment = "prod",
    [string]$Channel = "beta",
    [string]$UpdateBaseUrl = "",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "ROS-APP\ROS-APP.csproj"
$publishDir = Join-Path $root "ROS-APP\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"
$utf8Bom = [System.Text.UTF8Encoding]::new($true)

$channelName = if ([string]::IsNullOrWhiteSpace($Channel)) { "beta" } else { $Channel.Trim().ToLowerInvariant() }
if ($channelName -notin @("beta", "prod")) {
    throw "Invalid Channel: $Channel. Only beta/prod are supported."
}
$changelog = Join-Path $root ("CHANGELOG." + $channelName + ".txt")
$legacyChangelog = Join-Path $root "CHANGELOG.txt"

if ([string]::IsNullOrWhiteSpace($Environment)) {
    $Environment = $channelName
}

$distRoot = Join-Path $root ("dist\" + $channelName)
$outDir = Join-Path $root ("out\" + $channelName)

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

function Sanitize-ChangelogMessage([string]$message, [int]$maxLen = 180) {
    if ([string]::IsNullOrWhiteSpace($message)) {
        return "自动构建"
    }

    $fixed = $message -replace "(\r\n|\r|\n)+", " "
    $fixed = $fixed -replace "\s+", " "
    $fixed = $fixed.Trim()

    if ($fixed.Length -gt $maxLen) {
        return $fixed.Substring(0, $maxLen - 3).TrimEnd() + "..."
    }

    return $fixed
}

function Get-StatusLabel([string]$code) {
    switch ($code) {
        "M" { return "修改" }
        "A" { return "新增" }
        "D" { return "删除" }
        "R" { return "重命名" }
        "C" { return "复制" }
        "U" { return "冲突" }
        default { return $code }
    }
}

function Get-ChangeSummary([string]$repoRoot, [int]$take = 6) {
    $raw = @(git -c core.quotepath=false -C $repoRoot status --porcelain=v1 --untracked-files=no 2>$null)
    if (-not $raw) {
        return "自动构建"
    }

    $items = New-Object System.Collections.Generic.List[string]
    $total = 0
    foreach ($line in $raw) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.Length -lt 4) {
            continue
        }

        if ($line -notmatch "^\s*(?<code>[MADRCU\? ]{1,2})\s+(?<path>.+)$") {
            continue
        }

        $code = ($matches["code"] -replace "\s", "")
        if ([string]::IsNullOrWhiteSpace($code)) { $code = "M" }
        $statusCode = $code.Substring(0, 1)
        $path = $matches["path"].Trim()
        if ($path.Contains(" -> ")) {
            $path = ($path -split " -> ")[-1].Trim()
        }
        if ($path -match "^(dist|bin|obj|out)(/|\\)") {
            continue
        }

        $total += 1
        $items.Add("$(Get-StatusLabel $statusCode) $path")
        if ($items.Count -ge $take) { break }
    }

    if ($items.Count -eq 0) {
        return "自动构建"
    }

    $summary = "自动构建: " + ($items -join "；")
    if ($total -gt $items.Count) {
        $summary += "；共 $total 项"
    }
    return $summary
}

function Get-NextVersion([string]$version) {
    $parts = $version.Split('.')
    if ($parts.Count -lt 3) {
        return "0.0.1"
    }
    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    $patch = [int]$parts[2] + 1
    return "$major.$minor.$patch"
}

function Get-NotesForVersion([string]$path, [string]$version) {
    if (-not (Test-Path $path)) {
        return ""
    }

    $lines = [System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)
    $pattern = '^\[(?<ts>[^\]]+)\]\s+版本\s+(?<ver>\S+)\s+-\s+(?<status>成功|失败)\s+-\s+(?<msg>.+?)(\s+-\s+环境:\s*(?<env>\S+))?(\s+-\s+渠道:\s*(?<channel>\S+))?\s*$'
    for ($i = $lines.Length - 1; $i -ge 0; $i--) {
        $line = $lines[$i].Trim()
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -match $pattern -and $matches["ver"].Trim() -eq $version.Trim()) {
            return $matches["msg"].Trim()
        }
    }

    return ""
}

function Write-LatestJson([string]$version, [string]$baseUrl, [string]$notes, [string]$outDirPath) {
    if (-not (Test-Path $outDirPath)) {
        New-Item -ItemType Directory -Force -Path $outDirPath | Out-Null
    }

    $normalizedBase = Normalize-BaseUrl $baseUrl
    $downloadUrl = if ([string]::IsNullOrWhiteSpace($normalizedBase)) { "" } else { "${normalizedBase}ROS-APP-Setup-$version.exe" }

    $installerPath = Join-Path $outDirPath "ROS-APP-Setup-$version.exe"
    $sha256 = ""
    $buildTime = ""
    if (Test-Path $installerPath) {
        $sha256 = (Get-FileHash $installerPath -Algorithm SHA256).Hash
        $buildTime = (Get-Item $installerPath).LastWriteTime.ToString("yyyy-MM-ddTHH:mm:ssK")
    } else {
        $buildTime = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssK")
    }

    $latestJson = [ordered]@{
        version = $version
        downloadUrl = $downloadUrl
        notes = $notes
        sha256 = $sha256
        buildTime = $buildTime
    }
    $latestJsonPath = Join-Path $outDirPath "latest.json"
    [System.IO.File]::WriteAllText($latestJsonPath, ($latestJson | ConvertTo-Json -Depth 3) + "`r`n", $utf8Bom)
}

[xml]$xml = Get-Content $project
$versionNode = $xml.SelectSingleNode("//Project/PropertyGroup/Version")
if (-not $versionNode) {
    $pg = $xml.SelectSingleNode("//Project/PropertyGroup")
    if (-not $pg) {
        $pg = $xml.CreateElement("PropertyGroup")
        $null = $xml.Project.AppendChild($pg)
    }
    $versionNode = $xml.CreateElement("Version")
    $versionNode.InnerText = "0.0.1"
    $null = $pg.AppendChild($versionNode)
}

$currentVersion = $versionNode.InnerText
$nextVersion = Get-NextVersion $currentVersion
$versionNode.InnerText = $nextVersion
$xml.Save($project)

if ([string]::IsNullOrWhiteSpace($Message)) {
    $Message = Get-ChangeSummary -repoRoot $root
}
$Message = Sanitize-ChangelogMessage $Message

$distDir = Join-Path $distRoot ("ROS-APP_" + $nextVersion)
$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$publishSucceeded = $false
$keepLocales = @("zh-CN", "en-us")

try {
    $publishArgs = @(
        $project,
        "-c", "Release",
        "-r", "win-x64",
        "-p:Version=$nextVersion",
        "-p:PublishSingleFile=false",
        "-p:SelfContained=true",
        "-p:WindowsAppSDKSelfContained=true"
    )
    if ($Environment -eq "prod") {
        $publishArgs += @(
            "-p:DebugType=None",
            "-p:DebugSymbols=false",
            "-p:PublishReadyToRun=true"
        )
    }

    dotnet publish @publishArgs | Out-Host

    if (Test-Path $distDir) {
        Remove-Item -Recurse -Force $distDir
    }
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
    Copy-Item -Force -Recurse "$publishDir\*" $distDir

    if ($Environment -eq "prod") {
        Get-ChildItem -Path $distDir -Filter *.pdb -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force
        $localeDirs = Get-ChildItem -Path $distDir -Directory | Where-Object {
            $_.Name -match '^[a-z]{2,3}(-[A-Za-z0-9]{2,8})+$' -and ($keepLocales -notcontains $_.Name)
        }
        if ($localeDirs) {
            Remove-Item -Recurse -Force $localeDirs.FullName
        }
    }

    $publishSucceeded = $true
}
catch {
    $publishSucceeded = $false
    $err = $_.Exception.Message
}

$status = if ($publishSucceeded) { "成功" } else { "失败" }
$logLine = "[$timestamp] 版本 $nextVersion - $status - $Message - 环境: $Environment - 渠道: $channelName`r`n"
[System.IO.File]::AppendAllText($changelog, $logLine, $utf8Bom)
[System.IO.File]::AppendAllText($legacyChangelog, $logLine, $utf8Bom)

if ($publishSucceeded) {
    if ($SkipInstaller) {
        $notes = Get-NotesForVersion $changelog $nextVersion
        Write-LatestJson $nextVersion $UpdateBaseUrl $notes $outDir
    }
    else {
        powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "build_exe_installer.ps1") `
            -Channel $channelName `
            -Version $nextVersion `
            -OutputDir "out" `
            -UpdateBaseUrl $UpdateBaseUrl | Out-Host
    }
} else {
    throw "打包失败：$err"
}
