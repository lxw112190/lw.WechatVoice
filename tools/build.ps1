param([string]$Python='python', [switch]$SkipWorker)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
$asrStages=@()
try {
    & $Python -c "import sys,struct; assert sys.version_info[:2]==(3,12) and struct.calcsize('P')==8, 'Python 3.12 x64 is required'"
    if($LASTEXITCODE -ne 0){ throw 'Wrong Python environment.' }
    & $Python tools/verify-dependencies.py
    if($LASTEXITCODE -ne 0){ throw 'Pinned wheel check failed. See README for download command.' }
    & $Python tools/verify-silk.py
    if($LASTEXITCODE -ne 0){ throw 'Pinned SILK SDK source check failed.' }
    $env:PYTHONPATH=Join-Path $projectRoot '.deps/vendor'
    $env:PYTHONIOENCODING='utf-8'
    & $Python tests/test_core.py
    if($LASTEXITCODE -ne 0){ throw 'Synthetic core tests failed.' }
    $locator=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $msbuild=& $locator -latest -version '[17.0,18.0)' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if(-not $msbuild){ throw 'VS2022 MSBuild not found.' }
    & $msbuild WechatVoice.Workshop.sln /m /p:Configuration=Release /p:Platform=x64 /v:minimal /nologo
    if($LASTEXITCODE -ne 0){ throw 'WinForms build failed.' }
    if(-not $SkipWorker){
        & $Python -c "import PyInstaller; assert PyInstaller.__version__=='6.21.0', 'Use pinned PyInstaller 6.21.0'"
        if($LASTEXITCODE -ne 0){ throw 'PyInstaller 6.21.0 required.' }
        & $Python -m PyInstaller --noconfirm --onedir --name VoiceWorker --distpath dist --workpath build/pyinstaller --specpath build --paths .deps/vendor --hidden-import pysilk.backends.cython --collect-binaries pysilk --collect-binaries Cryptodome --exclude-module pysilk.backends.cffi --exclude-module cffi --exclude-module pycparser --exclude-module setuptools --exclude-module pkg_resources --exclude-module tkinter --exclude-module numpy --exclude-module IPython --exclude-module pytest worker/cli.py
        if($LASTEXITCODE -ne 0){ throw 'Worker build failed.' }
    }
    New-Item -ItemType Directory -Force build | Out-Null
    $compiler=Join-Path (Split-Path $msbuild) 'Roslyn/csc.exe'
    & $Python -c "import json,hashlib;from pathlib import Path;r=Path('.deps/asr-kids');d=json.loads(Path('asr-resources.json').read_text());assert all((r/v['path']).stat().st_size==v['bytes'] and hashlib.sha256((r/v['path']).read_bytes()).hexdigest()==v['sha256'] for v in d['files'])"
    if($LASTEXITCODE -ne 0){throw 'ASR resource hash mismatch. Run tools/import-kids-asr.py with the supplied r5 source.'}
    New-Item -ItemType Directory -Force dist/AsrWorker | Out-Null
    Copy-Item -Path .deps/asr-kids/* -Destination dist/AsrWorker -Recurse -Force
    Copy-Item -LiteralPath src/AsrWorker/bin/Release/AsrWorker.exe -Destination dist/AsrWorker/AsrWorker.exe -Force
    Copy-Item -LiteralPath src/AsrWorker/bin/Release/AsrWorker.exe.config -Destination dist/AsrWorker/AsrWorker.exe.config -Force
    & $compiler /nologo /langversion:7.3 /platform:x64 /target:exe /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Design.dll /out:build/FormsCheck.exe tests/FormsCheck.cs
    if($LASTEXITCODE -ne 0){ throw 'Designer/test harness compilation failed.' }
    & $compiler /nologo /langversion:7.3 /platform:x64 /target:exe /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /out:build/IpcCheck.exe tests/IpcCheck.cs src/WechatVoice.Workshop/WorkerClient.cs src/WechatVoice.Workshop/Diagnostics.cs
    if($LASTEXITCODE -ne 0){throw 'IPC harness compilation failed.'}
    & ./build/FormsCheck.exe ./src/WechatVoice.Workshop/bin/Release/WechatVoice.Workshop.exe ./test-results/designer
    if($LASTEXITCODE -ne 0){ throw 'DesignSurface check failed.' }
    & $compiler /nologo /langversion:7.3 /platform:x64 /target:exe /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /out:build/UiBehaviorCheck.exe tests/UiBehaviorCheck.cs src/WechatVoice.Workshop/UiPreferences.cs src/WechatVoice.Workshop/ExportForm.cs src/WechatVoice.Workshop/ExportForm.Designer.cs src/WechatVoice.Workshop/AccountScanner.cs
    if($LASTEXITCODE -ne 0){throw 'UI behavior harness compilation failed.'}
    & ./build/UiBehaviorCheck.exe (Join-Path $projectRoot ('test-results/ui-settings-'+[Guid]::NewGuid().ToString('N')))
    if($LASTEXITCODE -ne 0){throw 'UI behavior regression failed.'}
    & $Python tests/crypto_fixtures.py
    if($LASTEXITCODE -ne 0){throw 'Crypto fixture generation failed.'}
    & $compiler /nologo /langversion:7.3 /platform:x64 /target:exe /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /out:build/CryptoDbCheck.exe tests/CryptoDbCheck.cs
    if($LASTEXITCODE -ne 0){throw 'Crypto comparison harness compilation failed.'}
    & ./build/CryptoDbCheck.exe (Join-Path $projectRoot 'src/VoiceEngine/bin/Release/VoiceEngine.exe') (Join-Path $projectRoot 'test-results/crypto-fixtures')
    if($LASTEXITCODE -ne 0){throw 'C#/Python crypto comparison failed.'}
    & $compiler /nologo /langversion:7.3 /platform:x64 /target:exe /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /out:build/SnapshotCheck.exe tests/SnapshotCheck.cs
    if($LASTEXITCODE -ne 0){throw 'Snapshot harness compilation failed.'}
    & ./build/SnapshotCheck.exe (Join-Path $projectRoot 'src/VoiceEngine/bin/Release/VoiceEngine.exe') (Join-Path $projectRoot 'test-results/crypto-fixtures') (Join-Path $projectRoot ('test-results/snapshot-check-'+[Guid]::NewGuid().ToString('N')))
    if($LASTEXITCODE -ne 0){throw 'Snapshot read-only/progress/cancellation regression failed.'}
    & $compiler /nologo /langversion:7.3 /platform:x64 /target:exe /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /out:build/FailureDiagnosticsCheck.exe tests/FailureDiagnosticsCheck.cs
    if($LASTEXITCODE -ne 0){throw 'Failure diagnostics harness compilation failed.'}
    & ./build/FailureDiagnosticsCheck.exe (Join-Path $projectRoot 'src/VoiceEngine/bin/Release/VoiceEngine.exe') (Join-Path $projectRoot 'test-results/crypto-fixtures') (Join-Path $projectRoot ('test-results/failure-diagnostics-'+[Guid]::NewGuid().ToString('N')))
    if($LASTEXITCODE -ne 0){throw 'Failure diagnostics/privacy regression failed.'}
    & $Python tests/capture_fixtures.py
    if($LASTEXITCODE -ne 0){throw 'Capture fixture generation failed.'}
    & $compiler /nologo /langversion:7.3 /platform:x64 /target:exe /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /out:build/CaptureCheck.exe tests/CaptureCheck.cs
    if($LASTEXITCODE -ne 0){throw 'Capture comparison harness compilation failed.'}
    & ./build/CaptureCheck.exe (Join-Path $projectRoot 'src/VoiceEngine/bin/Release/VoiceEngine.exe') (Join-Path $projectRoot 'test-results/capture-fixtures') (Join-Path $projectRoot 'test-results/capture-temp')
    if($LASTEXITCODE -ne 0){throw 'C#/Python capture comparison failed.'}
    & $Python tests/library_fixtures.py
    if($LASTEXITCODE -ne 0){throw 'Library fixture generation failed.'}
    & $compiler /nologo /langversion:7.3 /platform:x64 /target:exe /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /out:build/LibraryCheck.exe tests/LibraryCheck.cs
    if($LASTEXITCODE -ne 0){throw 'Library comparison harness compilation failed.'}
    & ./build/LibraryCheck.exe (Join-Path $projectRoot 'src/VoiceEngine/bin/Release/VoiceEngine.exe') (Join-Path $projectRoot 'test-results/library-fixtures')
    if($LASTEXITCODE -ne 0){throw 'C#/Python library comparison failed.'}
    & cmd /c (Join-Path $projectRoot 'tools/build-silk.bat')
    if($LASTEXITCODE -ne 0){throw 'SILK SDK compiler failed; an older DLL will not be reused.'}
    if(-not (Test-Path -LiteralPath (Join-Path $projectRoot '.deps/silk-sdk/silk.dll'))){throw 'SILK SDK compile failed.'}
    Copy-Item -LiteralPath (Join-Path $projectRoot '.deps/silk-sdk/silk.dll') -Destination (Join-Path $projectRoot 'src/VoiceEngine/bin/Release/silk.dll') -Force
    & $Python tools/make-provenance.py
    if($LASTEXITCODE -ne 0){ throw 'Dependency provenance failed.' }
    & $Python tests/codec_fixtures.py
    if($LASTEXITCODE -ne 0){throw 'Codec fixture generation failed.'}
    & $compiler /nologo /langversion:7.3 /platform:x64 /target:exe /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /out:build/CodecCheck.exe tests/CodecCheck.cs
    if($LASTEXITCODE -ne 0){throw 'Codec comparison harness compilation failed.'}
    & ./build/CodecCheck.exe (Join-Path $projectRoot 'src/VoiceEngine/bin/Release/VoiceEngine.exe') (Join-Path $projectRoot 'test-results/codec-fixtures')
    if($LASTEXITCODE -ne 0){throw 'C#/Python codec comparison failed.'}
    & $compiler /nologo /langversion:7.3 /platform:x64 /target:exe /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /out:build/EngineRobustnessCheck.exe tests/EngineRobustnessCheck.cs
    if($LASTEXITCODE -ne 0){throw 'Engine robustness harness compilation failed.'}
    & ./build/EngineRobustnessCheck.exe (Join-Path $projectRoot 'src/VoiceEngine/bin/Release/VoiceEngine.exe') (Join-Path $projectRoot ('test-results/native-robustness-'+[Guid]::NewGuid().ToString('N')))
    if($LASTEXITCODE -ne 0){throw 'Engine robustness regression failed.'}
    if(Test-Path -LiteralPath (Join-Path $projectRoot 'dist/AsrWorker/AsrWorker.exe')){
        foreach($stage in @((Join-Path $projectRoot 'asr'),(Join-Path $projectRoot 'src/VoiceEngine/bin/Release/asr'))){
            $stage=[IO.Path]::GetFullPath($stage)
            if(-not $stage.StartsWith($projectRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe ASR staging path.'}
            $asrStages+=$stage
            if(Test-Path -LiteralPath $stage){Remove-Item -LiteralPath $stage -Recurse -Force}
            Copy-Item -LiteralPath (Join-Path $projectRoot 'dist/AsrWorker') -Destination $stage -Recurse
        }
    }
    & $Python tests/export_fixtures.py
    if($LASTEXITCODE -ne 0){throw 'Export fixture generation failed.'}
    & $compiler /nologo /langversion:7.3 /platform:x64 /target:exe /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /out:build/ExportCheck.exe tests/ExportCheck.cs
    if($LASTEXITCODE -ne 0){throw 'Export comparison harness compilation failed.'}
    & ./build/ExportCheck.exe (Join-Path $projectRoot 'src/VoiceEngine/bin/Release/VoiceEngine.exe') (Join-Path $projectRoot 'test-results/export-fixtures')
    if($LASTEXITCODE -ne 0){throw 'C#/Python export comparison failed.'}
    Write-Output 'Build and synthetic checks passed. See docs for the separately recorded real-account validation scope.'
} finally {
    foreach($stage in $asrStages){
        $resolvedStage=[IO.Path]::GetFullPath($stage)
        if(-not $resolvedStage.StartsWith($projectRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe ASR cleanup path.'}
        if(Test-Path -LiteralPath $resolvedStage){Remove-Item -LiteralPath $resolvedStage -Recurse -Force}
    }
    Pop-Location
}
