"""Isolated native UI, preferences and realtime checks. Does not use the live printer."""
from pathlib import Path
import os
import re
import socket
import subprocess
import time

root = Path(__file__).resolve().parents[1]
directory = root / 'artifacts/control-panel-qa'
directory.mkdir(exist_ok=True)
exe = root / 'vendor/Marlin-2.1.2.9/.pio/build/simulator_windows/debug/MarlinSimulator.exe'
env = dict(os.environ, PATH=str(root / '.tools/mingw64/bin') + os.pathsep + os.environ['PATH'])
port = 18199
settings = directory / 'simulator.settings'
settings.write_text('speed=1\njog_step=5\njog_feed=2400\nbed_target=61\ntarget0=205\n')

def launch(name, duration, reset=False, snapshot=False):
    args = [str(exe), '--port', str(port), '--quit-after', str(duration)]
    if reset:
        args.append('--reset-default')
    if snapshot:
        args += ['--snapshot', str(directory / 'panel.bmp')]
    startup = subprocess.STARTUPINFO()
    startup.dwFlags = subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = 0
    log = (directory / (name + '.log')).open('wb')
    process = subprocess.Popen(args, cwd=directory, env=env, startupinfo=startup, stdout=log, stderr=log)
    return process, log

process, output = launch('first', 24, snapshot=True)
try:
    deadline = time.monotonic() + 10
    while True:
        if process.poll() is not None:
            raise RuntimeError('Native window exited during startup')
        try:
            connection = socket.create_connection(('127.0.0.1', port), timeout=1)
            break
        except OSError:
            if time.monotonic() > deadline:
                raise TimeoutError('Native startup')
            time.sleep(.1)
    with connection:
        connection.settimeout(12)
        reader = connection.makefile('rb')
        def command(code):
            connection.sendall((code + '\n').encode('ascii'))
            lines = []
            while True:
                raw = reader.readline()
                if not raw:
                    raise RuntimeError('Firmware disconnected')
                line = raw.decode('ascii', errors='replace').strip()
                lines.append(line)
                if line.startswith('Error:'):
                    raise RuntimeError(line)
                if line == 'ok' or line.startswith('ok '):
                    return '\n'.join(lines)
        assert 'Marlin 2.1.2.9' in command('M115')
        timings = []
        for _ in range(3):
            started = time.monotonic()
            command('G4 P1000')
            timings.append(time.monotonic() - started)
        average = sum(timings) / len(timings)
        assert .75 < average < 1.5, timings
        print(f'PASS: Realtime, 1 s firmware delay averages {average:.3f} s wall time', flush=True)
        report = command('M119')
        for axis in 'xyz':
            assert axis + '_min:' in report and axis + '_max:' in report, report
        print('PASS: X-/X+, Y-/Y+, Z-/Z+ reported by Marlin', flush=True)
        command('M104 S50'); command('M140 S35')
        deadline = time.monotonic() + 12
        while True:
            report = command('M105')
            hotend = float(re.search(r'\bT:([\d.]+)', report)[1])
            bed = float(re.search(r'\bB:([\d.]+)', report)[1])
            if hotend >= 45 and bed >= 33:
                break
            if time.monotonic() > deadline:
                raise TimeoutError(report)
            time.sleep(.2)
        command('M104 S0'); command('M140 S0')
        print(f'PASS: virtual heaters, hotend {hotend:.1f} C, bed {bed:.1f} C', flush=True)
        reader.close()
    assert process.wait(timeout=30) == 0
finally:
    if process.poll() is None:
        process.terminate(); process.wait(timeout=5)
    output.close()

def values():
    return dict(line.split('=', 1) for line in settings.read_text().splitlines() if '=' in line)
saved = values()
assert saved['jog_step'] == '5' and saved['jog_feed'] == '2400' and saved['bed_target'] == '61' and saved['target0'] == '205'
for name, reset in [('restart', False), ('defaults', True)]:
    process, output = launch(name, 2, reset=reset)
    try:
        assert process.wait(timeout=15) == 0
    finally:
        if process.poll() is None:
            process.terminate(); process.wait(timeout=5)
        output.close()
    current = values()
    if reset:
        assert current['jog_step'] == '10' and current['jog_feed'] == '2000' and current['bed_target'] == '60' and current['target0'] == '200'
    else:
        assert current['jog_step'] == '5' and current['bed_target'] == '61' and current['target0'] == '205'
print('PASS: saved controls survive restart; Reset Default restores defaults', flush=True)
print('Preview:', directory / 'panel.bmp', flush=True)
