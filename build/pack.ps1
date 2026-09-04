# Builds the Winnow installer.
#
#   powershell -ExecutionPolicy Bypass -File build\pack.ps1 [-Version 1.0.1]
#
# Produces build\releases\Winnow-win-Setup.exe - a per-user installer that needs no
# administrator rights and no .NET runtime on the target machine.

param(
    [string] $Version = "1.0.0",

    # Signed with whichever certificate in the current user's store carries this subject.
    # Selecting by subject rather than by file means no .pfx and no password live anywhere:
    # the private key stays in the certificate store where Windows guards it.
    [string] $CertSubject = "CN=Winnow",

    # Without a timestamp a signature dies with the certificate, and every copy already
    # installed starts failing verification on the expiry date. With one it stays valid.
    [string] $TimestampUrl = "http://timestamp.digicert.com",

    [switch] $NoSign
)

$ErrorActionPreference = "Stop"

$repo  = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $PSScriptRoot "stage"
$out   = Join-Path $PSScriptRoot "releases"

# The install id is deliberately NOT "Winnow": Velopack installs under
# %LOCALAPPDATA%\<packId> and deletes that folder on uninstall, while the catalog lives in
# %LOCALAPPDATA%\Winnow. Sharing the name would make an uninstall destroy the catalog - and
# with it the record of every file still sitting in quarantine.
$packId = "WinnowApp"

Write-Host "== tests ==" -ForegroundColor Cyan
& dotnet test (Join-Path $repo "MediaTool.sln") --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "tests failed; not packaging" }

Write-Host "`n== publish ==" -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
New-Item -ItemType Directory -Path $out -Force | Out-Null

# Both executables land in one folder: the right-click menu and the shortcuts look for
# Winnow.exe beside winnow-cli.exe, and self-contained means no runtime to install first.
foreach ($project in @("src\MediaTool.App\MediaTool.App.csproj", "src\MediaTool.Cli\MediaTool.Cli.csproj")) {
    & dotnet publish (Join-Path $repo $project) -c Release -r win-x64 --self-contained true `
        --nologo -v q -o $stage
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $project" }
}

Copy-Item (Join-Path $repo "assets\winnow.ico") $stage -Force

# A unit test suite cannot see a startup crash: static initialisers, XAML resources and the
# entry point only run when the real executable does. Packaging an app that dies on launch
# is worse than not packaging at all, so it is opened once against a throwaway catalog.
Write-Host "`n== smoke test ==" -ForegroundColor Cyan
$smokeDb = Join-Path ([IO.Path]::GetTempPath()) ("winnow-smoke-" + [Guid]::NewGuid().ToString("N") + ".db")
$smoke = Start-Process (Join-Path $stage "Winnow.exe") -ArgumentList @("--db", $smokeDb) -PassThru
Start-Sleep -Seconds 12

if ($smoke.HasExited) {
    throw "the app exited on startup with code $($smoke.ExitCode) - not packaging. Check the Application event log."
}

$smoke.Kill()
$smoke.WaitForExit(10000) | Out-Null
Get-ChildItem ($smokeDb + "*") -EA SilentlyContinue | Remove-Item -Force -EA SilentlyContinue
Write-Host "app started and stayed up"

Write-Host "`n== pack ==" -ForegroundColor Cyan

$vpkArgs = @(
    "pack",
    "--packId", $packId, "--packVersion", $Version, "--packDir", $stage,
    "--mainExe", "Winnow.exe", "--packTitle", "Winnow", "--packAuthors", "Winnow",
    "--icon", (Join-Path $repo "assets\winnow.ico"), "--outputDir", $out
)

if (-not $NoSign) {
    $cert = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Subject -eq $CertSubject -and $_.HasPrivateKey } |
            Sort-Object NotAfter -Descending | Select-Object -First 1

    if ($cert) {
        Write-Host "signing with $($cert.Subject)  [$($cert.Thumbprint)]  expires $($cert.NotAfter.ToString('yyyy-MM-dd'))"
        $vpkArgs += @("--signParams", "/sha1 $($cert.Thumbprint) /fd SHA256 /tr $TimestampUrl /td SHA256")
    }
    else {
        # Said out loud rather than skipped quietly: an unsigned build looks identical until
        # somebody on another machine meets the SmartScreen wall.
        Write-Warning "No certificate with subject '$CertSubject' in Cert:\CurrentUser\My - building UNSIGNED."
        Write-Warning "Create one with:  New-SelfSignedCertificate -Type CodeSigningCert -Subject '$CertSubject' -CertStoreLocation Cert:\CurrentUser\My"
    }
}

& vpk @vpkArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

# The file name comes from the install id, which is not a name anyone should have to see.
# Renaming is safe: the setup bundle is self-contained and never refers to itself by name.
$built = Join-Path $out "$packId-win-Setup.exe"
$final = Join-Path $out "Winnow-Setup.exe"
if (Test-Path $built) { Move-Item $built $final -Force }

Write-Host "`n== done ==" -ForegroundColor Green
Get-ChildItem $out -Filter "*Setup.exe" | ForEach-Object {
    "{0}  ({1:N0} MB)" -f $_.FullName, ($_.Length / 1MB)
}
