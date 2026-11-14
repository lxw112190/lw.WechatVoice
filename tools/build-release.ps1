param([string]$Python='python')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $root
try {
    & $Python tools/check-repository.py
    if($LASTEXITCODE -ne 0){throw 'Public repository checks failed.'}
    & $Python tools/restore-dependencies.py
    if($LASTEXITCODE -ne 0){throw 'Dependency restore failed.'}
    & ./tools/build.ps1 -Python $Python -SkipWorker
    & ./tools/package.ps1 -Python $Python
    $delivery=Get-ChildItem -LiteralPath (Join-Path $root 'deliveries') -Directory | Sort-Object Name -Descending | Select-Object -First 1
    foreach($zip in Get-ChildItem -LiteralPath $delivery.FullName -Filter '*.zip'){
        $hash=(Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText($zip.FullName+'.sha256', $hash+'  '+$zip.Name+"`n",[Text.Encoding]::ASCII)
    }
    & $Python tools/verify-package.py (Join-Path $delivery.FullName ('WechatVoice-Workshop-'+(Get-Content VERSION -Raw).Trim()+'-win-x64.zip'))
    if($LASTEXITCODE -ne 0){throw 'Packaged runtime regression failed.'}
    & $Python tools/verify-source.py (Join-Path $delivery.FullName ('WechatVoice-Workshop-'+(Get-Content VERSION -Raw).Trim()+'-source.zip'))
    if($LASTEXITCODE -ne 0){throw 'Source ZIP clean compilation failed.'}
    Write-Output ('Release assets: '+$delivery.FullName)
} finally {Pop-Location}
