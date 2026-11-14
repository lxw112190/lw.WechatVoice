"""Compile exported Git-only source after restoring only declared cached downloads."""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def verify(archive):
    results = ROOT / 'test-results'
    results.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='public-source-', dir=results) as temporary:
        temp = Path(temporary).resolve()
        with zipfile.ZipFile(archive) as source_zip:
            assert source_zip.testzip() is None
            for name in source_zip.namelist():
                assert (temp / name).resolve().is_relative_to(temp)
                assert not {'.deps', 'workspace', 'bin', 'obj', '.git', 'test-results'}.intersection(Path(name).parts)
            source_zip.extractall(temp)
        source = temp / 'lw.WechatVoice'
        # These are only download caches; all native inputs must come from the ZIP.
        model = source / '.deps/asr-kids/models/sensevoice/model.int8.onnx'
        model.parent.mkdir(parents=True)
        shutil.copyfile(ROOT / '.deps/asr-kids/models/sensevoice/model.int8.onnx', model)
        shutil.copytree(ROOT / '.deps/wheels', source / '.deps/wheels')
        subprocess.run([sys.executable, 'tools/check-repository.py'], cwd=source, check=True)
        subprocess.run([sys.executable, 'tools/restore-dependencies.py', '--offline'], cwd=source, check=True)
        locator = Path(os.environ['ProgramFiles(x86)']) / 'Microsoft Visual Studio/Installer/vswhere.exe'
        msbuild = subprocess.check_output([str(locator), '-latest', '-version', '[17.0,18.0)', '-requires', 'Microsoft.Component.MSBuild', '-find', r'MSBuild\**\Bin\MSBuild.exe'], text=True).splitlines()[0]
        subprocess.run([msbuild, 'WechatVoice.Workshop.sln', '/m', '/p:Configuration=Release', '/p:Platform=x64', '/v:minimal', '/nologo'], cwd=source, check=True, timeout=120)
        subprocess.run([os.environ.get('COMSPEC', 'cmd.exe'), '/c', str(source / 'tools/build-silk.bat')], cwd=source, check=True, timeout=120)
        print('PASS: public Git-only ZIP CRC, publication boundary, declared cache restore, clean solution and SILK compilation.')


if __name__ == '__main__':
    verify(Path(sys.argv[1]).resolve())
