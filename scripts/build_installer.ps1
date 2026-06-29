param(
    [string]$OutputDir = "out",
    [string]$AppName = "ROS-APP",
    [string]$Publisher = "CN=Yuban-Network",
    [string]$PublisherDisplayName = "Yuban-Network",
    [string]$CertPassword = "rosapp"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "ROS-APP\ROS-APP.csproj"
$manifestSource = Join-Path $root "ROS-APP\Package.appxmanifest"
$assetsDir = Join-Path $root "ROS-APP\Assets"
$icon = Join-Path $root "icons\app.ico"

$outDir = Join-Path $root $OutputDir
$publishDir = Join-Path $outDir "publish"
$layoutDir = Join-Path $outDir "layout"
$manifestTarget = Join-Path $layoutDir "AppxManifest.xml"
$msixPath = Join-Path $outDir ("$AppName.msix")
$cerPath = Join-Path $outDir ("$AppName.cer")
$pfxPath = Join-Path $outDir ("$AppName.pfx")

if (Test-Path $outDir) {
    Remove-Item -Recurse -Force $outDir
}
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# Read version from csproj and build 4-part version for MSIX
[xml]$csproj = Get-Content $project
$versionNode = $csproj.SelectSingleNode("//Project/PropertyGroup/Version")
$version = if ($versionNode) { $versionNode.InnerText } else { "1.0.0" }
$appxVersion = "$version.0"

dotnet publish $project -c Release -r win-x64 -p:PublishSingleFile=false -p:SelfContained=true -p:WindowsAppSDKSelfContained=true | Out-Host

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $layoutDir | Out-Null

Copy-Item -Force -Recurse (Join-Path $root "ROS-APP\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\*") $layoutDir
Copy-Item -Force -Recurse $assetsDir (Join-Path $layoutDir "Assets")
if (Test-Path $icon) {
    Copy-Item -Force $icon (Join-Path $layoutDir "app.ico")
}

# Ensure base asset names exist for AppxManifest.xml
$assetsOut = Join-Path $layoutDir "Assets"
$assetMap = @{
    "Square150x150Logo.png" = "Square150x150Logo.scale-200.png"
    "Square44x44Logo.png" = "Square44x44Logo.scale-200.png"
    "Wide310x150Logo.png" = "Wide310x150Logo.scale-200.png"
    "SplashScreen.png" = "SplashScreen.scale-200.png"
}
foreach ($pair in $assetMap.GetEnumerator()) {
    $target = Join-Path $assetsOut $pair.Key
    $source = Join-Path $assetsOut $pair.Value
    if (-not (Test-Path $target) -and (Test-Path $source)) {
        Copy-Item -Force $source $target
    }
}

# Generate AppxManifest.xml from template
$xml = New-Object System.Xml.XmlDocument
$xml.PreserveWhitespace = $true
$xml.Load($manifestSource)
$ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
$ns.AddNamespace("m", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
$ns.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10")

$identity = $xml.SelectSingleNode("//m:Identity", $ns)
if ($identity) {
    $identity.SetAttribute("Name", $AppName)
    $identity.SetAttribute("Publisher", $Publisher)
    $identity.SetAttribute("Version", $appxVersion)
}

$props = $xml.SelectSingleNode("//m:Properties", $ns)
if ($props) {
    $props.SelectSingleNode("m:DisplayName", $ns).InnerText = $AppName
    $props.SelectSingleNode("m:PublisherDisplayName", $ns).InnerText = $PublisherDisplayName
}

$app = $xml.SelectSingleNode("//m:Applications/m:Application", $ns)
if ($app) {
    $app.SetAttribute("Executable", "$AppName.exe")
    $app.SetAttribute("EntryPoint", "Windows.FullTrustApplication")
}

$visual = $xml.SelectSingleNode("//uap:VisualElements", $ns)
if ($visual) {
    $visual.SetAttribute("DisplayName", $AppName)
    $visual.SetAttribute("Description", $AppName)
}

$xml.Save($manifestTarget)

$makeappx = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe"
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"

& $makeappx pack /d $layoutDir /p $msixPath | Out-Host

$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Publisher -CertStoreLocation "Cert:\CurrentUser\My" -KeyExportPolicy Exportable
$pwd = ConvertTo-SecureString -String $CertPassword -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pwd | Out-Null
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

& $signtool sign /fd SHA256 /a /f $pfxPath /p $CertPassword $msixPath | Out-Host

Write-Host "Installer: $msixPath"
Write-Host "Certificate: $cerPath"
