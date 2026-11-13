"""Download a private MinGW toolchain; no system installation or PATH changes."""
import concurrent.futures
import pathlib
import re
import subprocess
import urllib.request
import urllib.parse
import hashlib
import json
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
CACHE = ROOT / '.tools' / 'packages'
DEST = ROOT / '.tools'
BASE = 'https://repo.msys2.org/mingw/mingw64/'

def run(*args):
    return subprocess.check_output(args, text=True, encoding='utf-8', errors='replace')

def download(name):
    target = CACHE / name
    if not target.exists():
        print('Download', name, flush=True)
        part = target.with_suffix(target.suffix + '.part')
        subprocess.run(['curl.exe', '-sS', '-L', '--fail', '--retry', '2', '--connect-timeout', '20', '--max-time', '600', BASE + name, '-o', str(part)], check=True)
        part.replace(target)
    return target

def main():
    CACHE.mkdir(parents=True, exist_ok=True)
    platform = DEST / 'platform-native-1.2.1'
    if not platform.exists():
        archive = CACHE / 'platform-native-1.2.1.zip'
        subprocess.run(['curl.exe', '-sS', '-L', '--fail', '--retry', '2',
                        'https://github.com/platformio/platform-native/archive/refs/tags/v1.2.1.zip',
                        '-o', str(archive)], check=True)
        expected = '8b6692ebdb406be3bdf1fb7cd517e873256fe4d8ff02453d875fb55cecbce5a4'
        if hashlib.sha256(archive.read_bytes()).hexdigest() != expected:
            raise RuntimeError('SHA256 mismatch: platform-native')
        with zipfile.ZipFile(archive) as bundle:
            bundle.extractall(DEST)

    lock = ROOT / 'config/native-packages.lock.json'
    if lock.exists():
        entries = json.loads(lock.read_text())
        with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
            paths = list(pool.map(download, (entry['file'] for entry in entries)))
        for entry, path in zip(entries, paths):
            if hashlib.sha256(path.read_bytes()).hexdigest() != entry['sha256']:
                raise RuntimeError('SHA256 mismatch: ' + path.name)
            subprocess.run(['tar.exe', '-xf', str(path), '-C', str(DEST)], check=True)
        print('Ready:', DEST / 'mingw64' / 'bin')
        return
    index_path = ROOT / 'msys-packages.html'
    index = index_path.read_text() if index_path.exists() else urllib.request.urlopen(BASE).read().decode()
    names = {urllib.parse.unquote(n) for n in re.findall(r'href="([^"]+\.pkg\.tar\.zst)"', index)}
    def resolve(package):
        if package == 'mingw-w64-x86_64-vulkan':
            package = 'mingw-w64-x86_64-vulkan-loader'
        matches = [n for n in names if re.match(re.escape(package) + r'-\d', n)]
        if not matches:
            raise RuntimeError('Package missing: ' + package)
        return max(matches, key=lambda n: tuple(int(x) for x in re.findall(r'\d+', n[len(package):])))
    pending = {'mingw-w64-x86_64-' + n for n in ['gcc', 'SDL2', 'SDL2_net', 'glm', 'dlfcn']}
    done = set()
    manifest = []
    while pending:
        batch = sorted(pending - done)
        if not batch:
            break
        pending = set()
        with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
            paths = list(pool.map(download, map(resolve, batch)))
        for package, path in zip(batch, paths):
            info = run('tar.exe', '-xOf', str(path), '.PKGINFO')
            for dep in re.findall(r'^depend = (.+)$', info, re.M):
                dep = re.split('[<>=]', dep)[0]
                if dep.startswith('mingw-w64-x86_64-') and dep not in done:
                    pending.add(dep)
            print('Extract', package, flush=True)
            subprocess.run(['tar.exe', '-xf', str(path), '-C', str(DEST)], check=True)
            done.add(package)
            manifest.append({'file': path.name, 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()})
    (ROOT / 'config').mkdir(exist_ok=True)
    (ROOT / 'config/native-packages.lock.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print('Ready:', DEST / 'mingw64' / 'bin')

if __name__ == '__main__':
    main()
