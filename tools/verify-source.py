"""Verify an actual source ZIP, then compile its GUI, engine and native SILK SDK."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def verify(archive):
    archive = Path(archive).resolve()
    results = ROOT / 'test-results'
    results.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='source-rebuild-', dir=results) as temp:
        temp = Path(temp).resolve()
        assert temp.is_relative_to(results.resolve())
        with zipfile.ZipFile(archive) as zip:
            assert zip.testzip() is None
            for name in zip.namelist():
                assert (temp / name).resolve().is_relative_to(temp)
            zip.extractall(temp)
        source = temp / archive.stem
        for entry in json.loads((source / 'files.sha256.json').read_text(encoding='utf-8')):
            path = (source / entry['path']).resolve()
            assert path.is_relative_to(source)
            assert path.stat().st_size == entry['bytes']
            assert hashlib.sha256(path.read_bytes()).hexdigest() == entry['sha256']
        spec = importlib.util.spec_from_file_location('source_silk_verify', source / 'tools/verify-silk.py')
        module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
        module.verify(source)
        sdk = source / '.deps/silk-sdk'
        assert not any(path.suffix.lower() in {'.dll', '.obj', '.pdb', '.lib', '.exp'} for path in sdk.rglob('*'))
        subprocess.run([os.environ.get('COMSPEC', 'cmd.exe'), '/c', str(source / 'tools/build-silk.bat')], cwd=source, check=True, timeout=90)
        vswhere = Path(os.environ['ProgramFiles(x86)']) / 'Microsoft Visual Studio/Installer/vswhere.exe'
        msbuild = subprocess.check_output([str(vswhere), '-latest', '-version', '[17.0,18.0)', '-requires', 'Microsoft.Component.MSBuild', '-find', r'MSBuild\**\Bin\MSBuild.exe'], text=True).splitlines()[0]
        subprocess.run([msbuild, 'WechatVoice.Workshop.sln', '/m', '/p:Configuration=Release', '/p:Platform=x64', '/v:minimal', '/nologo'], cwd=source, check=True, timeout=90)
        assert (source / 'src/VoiceEngine/bin/Release/VoiceEngine.exe').is_file()
        assert (sdk / 'silk.dll').is_file()
        print('PASS: source ZIP CRC/hashes, pinned native sources/shim/scripts, clean SILK compilation and full solution build.')


if __name__ == '__main__':
    verify(sys.argv[1])
