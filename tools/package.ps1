param([string]$Python='python')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    $prepared=& $Python tools/package.py
    if($LASTEXITCODE -ne 0){throw 'Package staging failed.'}
    $paths=$prepared | ConvertFrom-Json
    $fixture=Join-Path $projectRoot ('test-results/package-fixture-'+[Guid]::NewGuid().ToString('N'))
    & $Python -c "import sys;sys.path[:0]=['tests','.deps/vendor'];from test_core import conversation_dataset;from pathlib import Path;conversation_dataset(Path(sys.argv[1]))" $fixture
    if($LASTEXITCODE -ne 0){throw 'Synthetic fixture creation failed.'}
    & ./build/IpcCheck.exe (Join-Path $paths.binary 'engine/VoiceWorker.exe') $fixture (Join-Path $paths.delivery 'ipc-workspace') 6 3
    if($LASTEXITCODE -ne 0){throw 'Final packaged IPC/count/cancellation check failed.'}
    $report=Join-Path $paths.delivery 'gui-test'
    & ./build/FormsCheck.exe (Join-Path $paths.binary 'WechatVoice.Workshop.exe') $report $fixture (Join-Path $paths.delivery 'test-export')
    if($LASTEXITCODE -ne 0){throw 'Final packaged GUI export check failed.'}
    # GUI verification produced private-shaped synthetic workspace; remove only this checked staging path.
    $runtimeWorkspace=[IO.Path]::GetFullPath((Join-Path $paths.binary 'workspace'))
    $expectedPrefix=[IO.Path]::GetFullPath($paths.binary)+[IO.Path]::DirectorySeparatorChar
    if(-not $runtimeWorkspace.StartsWith($expectedPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe workspace cleanup target.'}
    if(Test-Path -LiteralPath $runtimeWorkspace){Remove-Item -LiteralPath $runtimeWorkspace -Recurse -Force}
    & $Python -c "import sys;sys.path.insert(0,'tools');from pathlib import Path;from package import manifest,archive;folders=[Path(x) for x in sys.argv[1:]];[(manifest(p),archive(p,p.parent/(p.name+'.zip'))) for p in folders]" $paths.binary $paths.source
    if($LASTEXITCODE -ne 0){throw 'Zip/checksum creation failed.'}
    Write-Output ('Deployment: '+$paths.binary+'.zip')
    Write-Output ('Source: '+$paths.source+'.zip')
} finally {Pop-Location}
