param([switch]$Online, [switch]$InteractionTests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$app = Join-Path $projectRoot 'dist/HoverLex/HoverLex.exe'
$reportRoot = Join-Path $projectRoot ('artifacts/learning-quality-verified-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $reportRoot | Out-Null
$checks = @(
    @{Mode='self';Args=@('--self-test');Report='self-test.txt'},
    @{Mode='learning';Args=@('--learning-test');Report='learning-tests.txt'},
    @{Mode='hover';Args=@('--hover-translation-test');Report='hover-translation-tests.txt'},
    @{Mode='capture-compatibility';Args=@('--capture-compatibility-test');Report='capture-compatibility-tests.txt'},
    @{Mode='quality';Args=@('--quality-test');Report='quality-tests.txt'},
    @{Mode='correction';Args=@('--correction-test');Report='correction-tests.txt'},
    @{Mode='correction-toggle';Args=@('--correction-toggle-test');Report='correction-toggle-tests.txt'},
    @{Mode='bidirectional';Args=@('--bidirectional-test');Report='bidirectional-tests.txt'}
)
if ($InteractionTests) {
    $checks += @(
        @{Mode='native-input';Args=@('--translation-test');Report='translation-tests.txt'},
        @{Mode='correction-ui';Args=@('--correction-toggle-ui-test');Report='correction-toggle-ui-tests.txt'},
        @{Mode='selection';Args=@('--selection-test');Report='selection-tests.txt'},
        @{Mode='hover-controller';Args=@('--hover-controller-test');Report='hover-controller-tests.txt'},
        @{Mode='capture';Args=@('--integration-test');Report='integration-test.txt'}
    )
}
$results = @()
foreach ($check in $checks) {
    $arguments = @($check.Args)
    if ($Online -and $check.Mode -in @('hover','quality','correction','correction-toggle','bidirectional','native-input','selection')) { $arguments += '--online' }
    $process = Start-Process -FilePath $app -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    $report = Join-Path $projectRoot ('dist/HoverLex/' + $check.Report)
    Copy-Item -LiteralPath $report -Destination $reportRoot
    $passed = @(Select-String -LiteralPath $report -Pattern '^PASS ').Count
    $failed = @(Select-String -LiteralPath $report -Pattern '^FAIL |^BLOCKED ').Count
    $results += [pscustomobject]@{Mode=$check.Mode;Exit=$process.ExitCode;Passed=$passed;Problems=$failed;Report=$check.Report}
    Write-Output ($check.Mode + ': exit=' + $process.ExitCode + ', passed=' + $passed + ', problems=' + $failed)
}
foreach ($preview in @(Get-ChildItem (Join-Path $projectRoot 'dist/HoverLex') -File -Filter '*preview.png')) { Copy-Item -LiteralPath $preview.FullName -Destination $reportRoot }
[IO.File]::WriteAllText((Join-Path $reportRoot 'results.json'),($results | ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
Write-Output ('Reports: ' + $reportRoot)
if (@($results | Where-Object { $_.Exit -ne 0 -or $_.Problems -ne 0 }).Count -gt 0) { throw 'Some checks failed or were blocked; inspect the reports.' }
