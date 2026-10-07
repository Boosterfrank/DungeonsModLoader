<#
.SYNOPSIS
    Builds the DungeonsModLoader installer in one command.

.DESCRIPTION
    1. Runs the unit tests.
    2. Publishes the app as a self-contained single-file executable (no .NET runtime needed on the target PC).
    3. Compiles the Inno Setup installer.
    4. Writes dist\DungeonsModLoader-Setup-<version>.exe (plus a .sha256 file).

    The version comes from <Version> in Directory.Build.props. Code signing is a placeholder (see -Sign below).

.PARAMETER SkipTests
    Skip step 1.

.PARAMETER SkipInstaller
    Stop after publishing (no Inno Setup needed).

.PARAMETER Sign
    Sign the published exe and the installer with signtool. Disabled by default; fill in the certificate
    details in the "Code signing" block below before using it.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -SkipTests
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [switch]$Sign
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Step([string]$text) {
    Write-Host ""
    Write-Host ("==> " + $text) -ForegroundColor Cyan
}

function Invoke-Checked([string]$what, [scriptblock]$block) {
    & $block
    if ($LASTEXITCODE -ne 0) { throw ($what + " failed with exit code " + $LASTEXITCODE) }
}

# ---- Version (single source: Directory.Build.props) ----
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = $null
foreach ($group in $props.Project.PropertyGroup) { if ($group.Version) { $version = [string]$group.Version; break } }
if (-not $version) { throw "No <Version> found in Directory.Build.props" }

$solution = Join-Path $root "DungeonsModLoader.sln"
$appProject = Join-Path $root "src\DungeonsModLoader.App\DungeonsModLoader.App.csproj"
$publishDir = Join-Path $root ("publish\" + $Runtime)
$distDir = Join-Path $root "dist"
$exeName = "DungeonsModLoader.exe"

Step ("DungeonsModLoader " + $version + " (" + $Configuration + ", " + $Runtime + ")")

# ---- 1. Tests ----
if ($SkipTests) {
    Write-Host "Tests skipped (-SkipTests)."
} else {
    Step "Running tests"
    Invoke-Checked "dotnet test" { dotnet test $solution -c $Configuration --nologo -v minimal }
}

# ---- 2. Publish (self-contained, single file) ----
Step "Publishing"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
Invoke-Checked "dotnet publish" {
    # EnableCompressionInSingleFile is left off on purpose: it shrinks the installed exe but makes the installer
    # download larger (the bundle is then incompressible) and slows every start of the app.
    dotnet publish $appProject -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
        -o $publishDir --nologo
}
$exe = Join-Path $publishDir $exeName
if (-not (Test-Path $exe)) { throw ("Published executable not found: " + $exe) }
Write-Host ("Published " + $exe + " (" + [Math]::Round((Get-Item $exe).Length / 1MB, 1) + " MB)")

# ---- Code signing (placeholder, disabled by default) ----
# To enable: install a code-signing certificate, then run .\build.ps1 -Sign. Adjust the certificate selection
# below (/a picks the best available certificate; use /sha1 <thumbprint> or /f <pfx> /p <password> instead).
function Invoke-Sign([string]$file) {
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) { throw "signtool.exe was not found on PATH (it ships with the Windows SDK)." }
    Invoke-Checked "signtool" { & $signtool.Source sign /fd SHA256 /td SHA256 /tr "http://timestamp.digicert.com" /a $file }
}
if ($Sign) {
    Step "Signing the executable"
    Invoke-Sign $exe
} else {
    Write-Host "Code signing skipped (placeholder; run with -Sign once a certificate is set up)."
}

if ($SkipInstaller) {
    Step "Done (installer skipped)"
    return
}

# ---- 3. Installer ----
Step "Compiling the installer"
$candidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
)
$iscc = $null
foreach ($candidate in $candidates) { if ($candidate -and (Test-Path $candidate)) { $iscc = $candidate; break } }
if (-not $iscc) {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) was not found. Install it from https://jrsoftware.org/isdl.php" }

New-Item -ItemType Directory -Force $distDir | Out-Null
$script = Join-Path $root "installer\setup.iss"
Invoke-Checked "ISCC" { & $iscc ("/DMyAppVersion=" + $version) ("/DPublishDir=" + $publishDir) ("/O" + $distDir) /Qp $script }

$setup = Join-Path $distDir ("DungeonsModLoader-Setup-" + $version + ".exe")
if (-not (Test-Path $setup)) { throw ("Installer not found after compile: " + $setup) }

if ($Sign) {
    Step "Signing the installer"
    Invoke-Sign $setup
}

# ---- 4. Output ----
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path ($setup + ".sha256") -Value ($hash + " *" + (Split-Path $setup -Leaf)) -Encoding Ascii

Step "Done"
Write-Host $setup
Write-Host ("SHA-256: " + $hash)
Write-Host ("Size:    " + [Math]::Round((Get-Item $setup).Length / 1MB, 1) + " MB")
