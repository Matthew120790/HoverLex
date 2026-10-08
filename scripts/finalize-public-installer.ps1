param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$PublicKeyPath,
    [string]$Repository = 'Matthew120790/HoverLex',
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid GitHub repository.' }
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
$key = (Resolve-Path -LiteralPath $PublicKeyPath).Path
if ([IO.File]::ReadAllText($key) -match '<D>') { throw 'Only a public key may be embedded.' }
$version = [IO.File]::ReadAllText((Join-Path $projectRoot 'VERSION')).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid VERSION.' }
$package = Join-Path $output ('HoverLex-Windows-x64-v' + $version + '.zip')
$manifest = Join-Path $output 'latest.json'
$envelope = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
$payloadBytes = [Convert]::FromBase64String($envelope.Payload)
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
$rsa.PersistKeyInCsp = $false
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $rsa.FromXmlString([IO.File]::ReadAllText($key))
    if (-not $rsa.VerifyData($payloadBytes,$sha,[Convert]::FromBase64String($envelope.Signature))) { throw 'Release signature is invalid.' }
} finally { $sha.Dispose(); $rsa.Dispose() }
$payload = [Text.Encoding]::UTF8.GetString($payloadBytes) | ConvertFrom-Json
$expectedUrl = 'https://github.com/' + $Repository + '/releases/download/v' + $version + '/' + [IO.Path]::GetFileName($package)
if ($payload.Version -ne $version -or $payload.Package -ne $expectedUrl -or $payload.Size -ne (Get-Item -LiteralPath $package).Length -or $payload.Sha256 -ne (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash) { throw 'Package does not match the signed release.' }
$channel = Join-Path $output 'update-channel.json'
[IO.File]::WriteAllText($channel,(@{Source=('https://github.com/' + $Repository + '/releases/latest/download/latest.json')} | ConvertTo-Json -Compress),(New-Object Text.UTF8Encoding($false)))
$migration = Join-Path $output 'migration-source.txt'
[IO.File]::WriteAllText($migration,'',(New-Object Text.UTF8Encoding($false)))
$versionSource = Join-Path $output 'VersionInfo.cs'
$versionText = 'using System.Reflection;' + [Environment]::NewLine + '[assembly: AssemblyFileVersion("' + $version + '.0")]' + [Environment]::NewLine + '[assembly: AssemblyVersion("' + $version + '.0")]'
[IO.File]::WriteAllText($versionSource,$versionText,(New-Object Text.UTF8Encoding($false)))
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$setup = Join-Path $output ('HoverLex-Setup-v' + $version + '.exe')
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001','/utf8output',('/out:' + $setup),('/win32manifest:' + (Join-Path $projectRoot 'app.manifest')))
$arguments += '/win32icon:' + (Join-Path $projectRoot 'assets/HoverLex.ico')
foreach ($resource in @(@((Join-Path $projectRoot 'assets/HoverLex.ico'),'hoverlex-icon'),@((Join-Path $projectRoot 'assets/HoverLex-mark.png'),'hoverlex-mark'),@($key,'update-public-key'),@($package,'initial-package'),@($manifest,'initial-release'),@($channel,'default-channel'),@($migration,'migration-source'))) { $arguments += '/resource:' + $resource[0] + ',' + $resource[1] }
foreach ($name in @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll')) { $arguments += '/reference:' + (Join-Path $framework $name) }
$arguments += @((Join-Path $projectRoot 'src/Design.cs'),(Join-Path $projectRoot 'desktop/UpdateCore.cs'),$versionSource,(Join-Path $projectRoot 'desktop/Installer.cs'))
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw 'Public installer build failed.' }
$checksums = @($setup,$package,$manifest) | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_) }
[IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'),$checksums,(New-Object Text.UTF8Encoding($false)))
if ($Test) {
    $target = Join-Path $output 'test-install'
    & (Join-Path $PSScriptRoot 'install-silent.ps1') -InstallerPath $setup -Target $target -Desktop (Join-Path $output 'test-desktop') -StartMenu (Join-Path $output 'test-start-menu') -SkipIconRefresh | Out-Null
    if (@(Get-ChildItem -LiteralPath (Join-Path $target 'UserData') -File).Count -ne 0) { throw 'Unexpected user data in public installation.' }
    $installedChannel = Get-Content -LiteralPath (Join-Path $target 'update-channel.json') -Raw | ConvertFrom-Json
    if ($installedChannel.Source -ne ('https://github.com/' + $Repository + '/releases/latest/download/latest.json')) { throw 'Incorrect update channel.' }
    $pointer = Get-Content -LiteralPath (Join-Path $target 'current.json') -Raw | ConvertFrom-Json
    $installedApp = Join-Path (Join-Path $target $pointer.Directory) 'HoverLex.exe'
    $process = Start-Process -FilePath $installedApp -ArgumentList '--correction-test' -WindowStyle Hidden -Wait -PassThru
    Get-Content -LiteralPath (Join-Path (Split-Path -Parent $installedApp) 'correction-tests.txt')
    if ($process.ExitCode -ne 0) { throw 'Installed proofreading tests failed.' }
    Write-Host 'PASS public installation, shortcuts, empty user data, HTTPS channel and proofreading'
}
Write-Host ('Public installer ready: ' + $setup)
