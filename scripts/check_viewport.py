"""Isolated viewport snapshot with nozzle above the bed; no live printer used."""
from pathlib import Path
import os, socket, subprocess, time
root = Path(__file__).resolve().parents[1]
directory = root / 'artifacts/viewport-qa'
directory.mkdir(exist_ok=True)
(directory / 'simulator.settings').write_text('speed=20\n')
exe = root / 'vendor/Marlin-2.1.2.9/.pio/build/simulator_windows/debug/MarlinSimulator.exe'
env = dict(os.environ, PATH=str(root / '.tools/mingw64/bin') + os.pathsep + os.environ['PATH'])
startup = subprocess.STARTUPINFO()
startup.dwFlags = subprocess.STARTF_USESHOWWINDOW
startup.wShowWindow = 0
with (directory / 'run.log').open('wb') as log:
    process = subprocess.Popen([str(exe), '--port', '18201', '--quit-after', '4', '--snapshot', str(directory / 'viewport.bmp')], cwd=directory, env=env, startupinfo=startup, stdout=log, stderr=log)
    try:
        deadline = time.monotonic() + 8
        while True:
            try:
                connection = socket.create_connection(('127.0.0.1',18201),timeout=.2)
                break
            except OSError:
                if process.poll() is not None or time.monotonic()>deadline: raise
                time.sleep(.02)
        with connection:
            connection.settimeout(5)
            connection.sendall(b'G90\nG1 X100 Y100 Z30 F3000\nM400\nM114\n')
            response = b''
            while b'X:100.00 Y:100.00 Z:30.00' not in response:
                part=connection.recv(4096)
                if not part: raise RuntimeError(response)
                response+=part
        assert process.wait(timeout=12)==0
        assert (directory/'viewport.bmp').stat().st_size>10000
        print('PASS: XYZ movement, viewport snapshot, clean shutdown')
    finally:
        if process.poll() is None:
            process.terminate(); process.wait(timeout=5)
