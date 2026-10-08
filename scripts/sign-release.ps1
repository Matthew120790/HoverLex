param([Parameter(Mandatory=$true)][string]$PayloadPath, [Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$keyRoot = Join-Path $projectRoot '.release-signing'
New-Item -ItemType Directory -Path $keyRoot -Force | Out-Null
$privatePath = Join-Path $keyRoot 'private.xml'
$publicPath = Join-Path $keyRoot 'public.xml'
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider(2048)
$rsa.PersistKeyInCsp = $false
try {
    if (Test-Path -LiteralPath $privatePath) {
        $rsa.FromXmlString([IO.File]::ReadAllText($privatePath))
    } else {
        [IO.File]::WriteAllText($privatePath, $rsa.ToXmlString($true), (New-Object Text.UTF8Encoding($false)))
        [IO.File]::WriteAllText($publicPath, $rsa.ToXmlString($false), (New-Object Text.UTF8Encoding($false)))
    }
    $payload = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $PayloadPath).Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $signature = $rsa.SignData($payload, $sha) } finally { $sha.Dispose() }
    $envelope = @{ Payload = [Convert]::ToBase64String($payload); Signature = [Convert]::ToBase64String($signature) } | ConvertTo-Json -Compress
    $outputFull = [IO.Path]::GetFullPath($OutputPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputFull)) | Out-Null
    $temporary = $outputFull + '.new'
    [IO.File]::WriteAllText($temporary, $envelope, (New-Object Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $outputFull) { [IO.File]::Replace($temporary, $outputFull, $outputFull + '.bak') }
    else { [IO.File]::Move($temporary, $outputFull) }
} finally { $rsa.Dispose() }
Write-Host ('Signed manifest: ' + $OutputPath)
