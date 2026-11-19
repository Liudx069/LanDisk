param(
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][string]$EvidenceDir,
    [string]$Repo = '',
    # The capture writes TSVs and PNGs, not build-info.json, so the commit being built is supplied by the caller.
    [string]$CandidateCommit = '',
    # A reduced matrix is only legitimate for a targeted chain test, and it is recorded when declared.
    [switch]$ReducedMatrix,
    [switch]$SkipVerdict
)

# The desktop-matrix gate used by the real release entry point (tools/Build-All.ps1).
#
# It exists as its own script so that the release flow and the blocking test exercise the *same* rule. A test of
# a stand-alone verdict function proves nothing about a release entry point that never calls it — which was
# exactly the situation before: Build-All never ran the matrix at all, so a failing matrix could not stop a
# release no matter what any function returned.

$ErrorActionPreference = 'Stop'
if (-not $Repo) { $Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }

$record = [ordered]@{
    entryPoint   = 'tools/Invoke-UiMatrixGate.ps1'
    calledFrom   = if ($env:LANDISK_GATE_CALLER) { $env:LANDISK_GATE_CALLER } else { 'direct' }
    publishDir   = $PublishDir
    evidenceDir  = $EvidenceDir
    exePath      = ''
    exeSha256    = ''
    exitCode     = $null
    verdict      = ''
    reducedMatrix = [bool]$ReducedMatrix
    blocked      = $false
    blockReason  = ''
    readyReportWritten = $false
    packageWritten     = $false
    checkedAt    = (Get-Date -Format 'o')
}

function Save-Record {
    New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null
    $record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $EvidenceDir 'UI_MATRIX_GATE.json') -Encoding UTF8
}

try {
    $exe = Join-Path $PublishDir 'LanDisk.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "发布目录缺少 LanDisk.exe：$PublishDir" }
    $record.exePath = $exe
    $record.exeSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()

    if (-not (Test-Path -LiteralPath $EvidenceDir)) { New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null }

    # Wait for the capture properly.
    #
    # LanDisk.exe is a Windows-subsystem (GUI) executable. PowerShell's call operator does NOT wait for a GUI
    # process, so `& $exe ...` returned immediately: $LASTEXITCODE was null and the output files were not there
    # yet, which made the gate block a perfectly good run and would have blocked every real release too.
    # Start-Process -Wait -PassThru waits for the process and reports its real exit code.
    $proc = Start-Process -FilePath $exe -ArgumentList @('--render-ui', $EvidenceDir) -Wait -PassThru -NoNewWindow
    $record.exitCode = $proc.ExitCode
    Write-Host ("界面矩阵退出码: {0}" -f $record.exitCode)

    # Layer 2: the capture's own verdict, read from the files it wrote.
    if (-not $SkipVerdict) {
        . (Join-Path $PSScriptRoot 'EvidenceVerdict.ps1')
        # The capture writes TSVs and PNGs, not build-info.json, so the expected candidate is supplied by the
        # caller (Build-All knows the commit it is building). Without it the verdict could not run at all and
        # would block every release, which is fail-closed but useless.
        if (-not $CandidateCommit) { $CandidateCommit = (& git -C $Repo rev-parse HEAD).Trim() }
        $record | Add-Member -NotePropertyName candidateCommit -NotePropertyValue $CandidateCommit -Force
        $verdict = Get-EvidenceVerdict -EvidenceRoot $EvidenceDir -ExpectedCandidate $CandidateCommit -SkipMatrixCompleteness:$ReducedMatrix -Scope UI
        $record.verdict = Format-Verdict $verdict
    }

    # Layer 3: the release decision. Non-zero capture, a NOT_READY verdict, or a missing verdict all block.
    $reasons = @()
    if ($record.exitCode -ne 0) { $reasons += ("界面矩阵退出码 {0}" -f $record.exitCode) }
    if (-not $SkipVerdict -and $record.verdict -notlike 'READY*') { $reasons += ('证据判定 ' + $record.verdict) }
    if (-not (Test-Path -LiteralPath (Join-Path $EvidenceDir 'UI_CAPTURE.tsv'))) { $reasons += '缺少 UI_CAPTURE.tsv' }

    if ($reasons.Count -gt 0) {
        $record.blocked = $true
        $record.blockReason = ($reasons -join ' ; ')
        Save-Record
        Write-Host ("发布被阻止：{0}" -f $record.blockReason) -ForegroundColor Red
        exit 4
    }

    $record.verdict = if ($record.verdict) { $record.verdict } else { 'SKIPPED' }
    Save-Record
    Write-Host '界面矩阵通过，发布继续。' -ForegroundColor Green
    exit 0
}
catch {
    $record.blocked = $true
    $record.blockReason = $_.Exception.Message
    Save-Record
    Write-Host ("发布被阻止：{0}" -f $_.Exception.Message) -ForegroundColor Red
    exit 4
}
