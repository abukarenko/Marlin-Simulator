"""Check consecutive native starts with the existing saved panel layout."""
from pathlib import Path
import socket
import subprocess
import time

root = Path(__file__).resolve().parents[1]
directory = root / 'dist/firmware'
assert (directory / 'imgui.ini').exists(), 'A saved imgui.ini is required for this regression check'
for attempt in range(1, 4):
    startup = subprocess.STARTUPINFO()
    startup.dwFlags = subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = 0
    with (root / f'artifacts/restart-{attempt}.log').open('wb') as output:
        process = subprocess.Popen([str(directory / 'MarlinSimulator.exe')], cwd=directory,
                                   startupinfo=startup, stdout=output, stderr=output)
        try:
            deadline = time.monotonic() + 10
            while True:
                if process.poll() is not None:
                    raise RuntimeError(f'Start {attempt} crashed: {process.returncode}')
                try:
                    connection = socket.create_connection(('127.0.0.1', 8099), timeout=1)
                    break
                except OSError:
                    if time.monotonic() > deadline:
                        raise TimeoutError('Native startup timed out')
                    time.sleep(.1)
            with connection:
                connection.settimeout(10)
                connection.sendall(b'M115\n')
                reader = connection.makefile('rb')
                found = False
                while True:
                    raw = reader.readline()
                    if not raw:
                        raise RuntimeError('Firmware disconnected')
                    line = raw.decode('ascii', errors='replace').strip()
                    found |= 'FIRMWARE_NAME:Marlin 2.1.2.9' in line
                    if line == 'ok' or line.startswith('ok '):
                        break
                reader.close()
                assert found, 'Firmware version reply missing'
            assert process.poll() is None, 'Native window exited'
            print(f'PASS: saved-layout restart {attempt}, Marlin 2.1.2.9 responded', flush=True)
        finally:
            if process.poll() is None:
                process.terminate()
            process.wait(timeout=5)
