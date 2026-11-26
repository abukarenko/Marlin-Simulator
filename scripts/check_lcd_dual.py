"""Verify the dual-hotend / BLTouch profile against real Marlin, on loopback only."""
from pathlib import Path
import argparse
import re
import socket
import subprocess
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', type=Path, default=ROOT / 'dist/lcd/EasyMarlinEmulator.exe')
    exe = parser.parse_args().exe.resolve()
    artifacts = ROOT / 'artifacts'
    artifacts.mkdir(exist_ok=True)
    with socket.socket() as reservation:
        reservation.bind(('127.0.0.1', 0))
        port = reservation.getsockname()[1]
    startup = subprocess.STARTUPINFO()
    startup.dwFlags = subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = 0
    with tempfile.TemporaryDirectory(prefix='marlin-dual-') as work, (artifacts / 'lcd-dual-check.log').open('w', encoding='utf-8') as log:
        process = subprocess.Popen([str(exe), '--port', str(port), '--quit-after', '360',
                                    '--snapshot', str(artifacts / 'lcd-dual-check.bmp'),
                                    '--snapshot-after', '10'], cwd=work, startupinfo=startup)
        try:
            deadline = time.monotonic() + 20
            while True:
                try:
                    connection = socket.create_connection(('127.0.0.1', port), timeout=1)
                    break
                except OSError:
                    if process.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError('Virtual printer did not start')
                    time.sleep(0.1)
            connection.settimeout(120)
            with connection, connection.makefile('rb') as reader:
                def command(value):
                    print('> ' + value, flush=True)
                    log.write('> ' + value + '\n'); log.flush()
                    connection.sendall((value + '\n').encode('ascii'))
                    lines = []
                    started = time.monotonic()
                    while True:
                        raw = reader.readline()
                        assert raw, 'Firmware disconnected'
                        line = raw.decode('ascii', errors='replace').strip()
                        lines.append(line)
                        log.write('< ' + line + '\n'); log.flush()
                        assert not line.startswith(('Error:', '!!', 'Resend:')) and 'Unknown command' not in line, line
                        assert time.monotonic() - started < 150, f'Timed out: {value}'
                        if line == 'ok' or line.startswith('ok '):
                            return '\n'.join(lines)

                def position(x, y, z):
                    reply = command('M114')
                    match = re.search(r'X:([-\d.]+) Y:([-\d.]+) Z:([-\d.]+)', reply)
                    assert match, reply
                    for actual, expected in zip(map(float, match.groups()), (x, y, z)):
                        assert abs(actual - expected) < 0.03, reply

                def targets(t0, t1, bed):
                    reply = command('M105')
                    for name, target in (('T0', t0), ('T1', t1), ('B', bed)):
                        match = re.search(r'\b' + name + r':[-\d.]+\s*/\s*([-\d.]+)', reply)
                        assert match and abs(float(match.group(1)) - target) < 0.1, reply

                command('M110 N0')
                assert 'EXTRUDER_COUNT:2' in command('M115')
                command('M502')  # Only the disposable virtual EEPROM is reset.
                offsets = command('M218')
                assert re.search(r'M218 T1 X46\.0+ Y0\.0+ Z0\.0+', offsets), offsets
                probe = command('M851')
                assert 'X23.00 Y-44.00 Z-2.00' in probe, probe
                settings = command('M503')
                assert 'M92' in settings and 'T1' in settings, settings
                command('M401')
                command('M402')
                command('G28')
                command('G90')
                command('G1 X80 Y100 Z5 F3000')
                command('M400')
                position(80, 100, 5)
                command('T1')
                command('M400')
                position(80, 100, 5)
                command('T0')
                command('M400')
                position(80, 100, 5)
                command('G29')
                mesh = command('M420 V')
                assert 'Bilinear Leveling Grid' in mesh, mesh
                command('M420 S1')
                command('M104 T0 S50')
                command('M104 T1 S65')
                command('M140 S40')
                targets(50, 65, 40)
                command('M109 T1 S65')
                command('M109 T0 S50')
                targets(50, 65, 40)
                command('M302 S0')  # Virtual cold extrusion for independent E0/E1 motion checks.
                command('M83')
                for tool in ('T0', 'T1'):
                    command(tool)
                    command('G1 E1 F120')
                    command('G1 E-1 F120')
                    command('M400')
                command('M500')
                command('M218 T1 X45.95 Y0.05')
                changed = command('M218')
                assert 'X45.95 Y0.05' in changed, changed
                command('M501')
                restored = command('M218')
                assert 'X46.00 Y0.00 Z0.000' in restored, restored
                command('M104 T0 S50')
                command('M104 T1 S65')
                command('M140 S40')
                print('> M411', flush=True)
                log.write('> M411\n'); log.flush()
                connection.sendall(b'M411\n')
                stopped = ''
                while 'HOST_STOP_DONE V1' not in stopped:
                    line = reader.readline().decode('ascii', errors='replace').strip()
                    assert line, 'Disconnected during STOP'
                    log.write('< ' + line + '\n'); log.flush()
                    stopped += '\n' + line
                targets(0, 0, 0)
                command('M104 T0 S50')
                command('M104 T1 S65')
                connection.sendall(b'M112\n')
                killed = []
                deadline = time.monotonic() + 10
                while True:
                    line = reader.readline().decode('ascii', errors='replace').strip()
                    assert line, 'Disconnected before emergency stop acknowledgement'
                    killed.append(line)
                    log.write('< ' + line + '\n'); log.flush()
                    if 'Printer halted' in line or 'kill()' in line:
                        break
                    assert time.monotonic() < deadline, killed
        finally:
            if process.poll() is None:
                process.terminate()
            process.wait(timeout=10)
    print('PASS: two tools, XY offsets, BLTouch homing/mesh, independent heat, E0/E1 commands, EEPROM and STOP.', flush=True)


if __name__ == '__main__':
    main()
