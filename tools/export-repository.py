"""Export the public Git tree with explicit allowlists, never the working directory."""
import argparse
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parents[1]
FILES = ['README.md', 'README.en.md', 'LICENSE', 'VERSION', 'CHANGELOG.md',
         'CONTRIBUTING.md', 'SECURITY.md', 'AGENTS.md', '.gitignore', '.gitattributes',
         '.editorconfig', 'WechatVoice.Workshop.sln', 'requirements-lock.txt',
         'asr-resources.json', 'THIRD-PARTY-NOTICES.md']
DIRS = ['src', 'worker', 'tests', 'tools', 'licenses', 'docs', '.github', 'third_party']
SKIP = {'bin', 'obj', '__pycache__', '.vs'}
PRIVATE = {'.db', '.sqlite', '.sqlite3', '.silk', '.wav', '.pcm', '.dmp', '.key', '.dpapi', '.log', '.user', '.suo'}


def public_files():
    for name in FILES:
        yield ROOT / name
    for name in DIRS:
        for path in sorted((ROOT / name).rglob('*')):
            if path.is_file() and not SKIP.intersection(path.relative_to(ROOT).parts):
                if path.suffix.lower() in PRIVATE:
                    raise ValueError('Private artifact in public source tree: ' + path.relative_to(ROOT).as_posix())
                yield path


def export(output):
    output = Path(output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output, 'x', zipfile.ZIP_DEFLATED) as archive:
        for path in public_files():
            archive.write(path, 'lw.WechatVoice/' + path.relative_to(ROOT).as_posix())
    print(output)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', required=True)
    export(parser.parse_args().output)
