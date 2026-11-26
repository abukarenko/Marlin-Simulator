"""Integration check against the real LCD firmware, using an isolated TCP port."""
from pathlib import Path
import argparse
import socket
import subprocess
import time
import tempfile

ROOT = Path(__file__).resolve().parents[1]
EXE = ROOT / 'dist/lcd/EasyMarlinEmulator.exe'
ARTIFACTS = ROOT / 'artifacts'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', type=Path, default=EXE, help='Executable to verify, including an unpacked release')
    exe = parser.parse_args().exe.resolve()
    ARTIFACTS.mkdir(exist_ok=True)
    workspace = tempfile.TemporaryDirectory(prefix='marlin-lcd-qa-')
    work = Path(workspace.name)
    # Let Windows choose a free loopback port for this run.
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        port = probe.getsockname()[1]
    snapshot = ARTIFACTS / 'lcd-smoke.bmp'
    snapshot.unlink(missing_ok=True)
    transcript = []
    def record(line):
        transcript.append(line)
        with (ARTIFACTS / 'lcd-smoke-live.log').open('a', encoding='utf-8') as log:
            log.write(line + '\n')
    startup = subprocess.STARTUPINFO()
    startup.dwFlags = subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = 0
    process = subprocess.Popen([str(exe), '--port', str(port), '--scale', '4',
                                '--quit-after', '50', '--snapshot', str(snapshot),
                                '--snapshot-after', '20'],
                               cwd=work, startupinfo=startup)
    try:
        deadline = time.monotonic() + 15
        while True:
            try:
                connection = socket.create_connection(('127.0.0.1', port), timeout=1)
                break
            except OSError:
                assert process.poll() is None, 'LCD process exited during startup'
                if time.monotonic() > deadline:
                    raise TimeoutError('LCD TCP listener did not start')
                time.sleep(0.1)
        connection.settimeout(15)
        with connection, connection.makefile('rb') as reader:
            def send(command):
                record('> ' + command)
                connection.sendall((command + '\n').encode('ascii'))

            def read():
                raw = reader.readline()
                assert raw, 'Firmware disconnected unexpectedly'
                line = raw.decode('ascii', errors='replace').strip()
                record('< ' + line)
                return line

            def command(value):
                send(value)
                replies = []
                while True:
                    line = read()
                    replies.append(line)
                    assert not line.startswith(('Error:', 'Resend:', '!!')) and 'Unknown command' not in line, line
                    if line == 'ok' or line.startswith('ok '):
                        return '\n'.join(replies)

            command('M110 N0')
            assert 'Marlin' in command('M115')
            temperature = command('M105')
            assert 'T:' in temperature and 'B:' in temperature
            numbered = 'N1 M105'
            checksum = 0
            for byte in numbered.encode('ascii'):
                checksum ^= byte
            command(f'{numbered}*{checksum}')
            send('N2 M105*0')
            while not read().startswith('Resend:'):
                pass
            # Drain the accompanying ok, then recover line numbering.
            while not read().startswith('ok'):
                pass
            command('M110 N0')
            command('G28')
            command('G1 X20 Y30 Z2 F3000')
            command('M400')
            position = command('M114')
            assert 'X:20.00 Y:30.00 Z:2.00' in position, position
            command('M104 S50')
            command('M140 S40')
            command('M106 S128')
            command('M73 P42')
            command('M117 TCP LCD test')
            # Leave the status screen visible until the native snapshot is taken.
            time.sleep(3)
            heated = command('M105')
            assert '/50' in heated and '/40' in heated, heated
            deadline = time.monotonic() + 25
            while not snapshot.exists():
                assert time.monotonic() < deadline, 'Timed out waiting for LCD snapshot'
                time.sleep(0.1)
        # Reconnecting must preserve printer state and accept fresh commands.
        time.sleep(0.2)
        with socket.create_connection(('127.0.0.1', port), timeout=5) as again:
            again.settimeout(10)
            with again.makefile('rb') as reader:
                again.sendall(b'M114\n')
                replies = []
                while True:
                    line = reader.readline().decode('ascii', errors='replace').strip()
                    assert line, 'Reconnect failed'
                    replies.append(line)
                    if line.startswith('ok'):
                        break
                assert any('X:20.00 Y:30.00 Z:2.00' in line for line in replies)
                # Emergency parser must break a blocking heat wait immediately.
                again.sendall(b'M109 S200\n')
                while True:
                    raw = reader.readline()
                    assert raw, 'Disconnected during M109'
                    record('< ' + raw.decode('ascii', errors='replace').strip())
                    if raw.lstrip().startswith(b'T:'):
                        break
                started = time.monotonic()
                again.sendall(b'M112\n')
                while True:
                    line = reader.readline().decode('ascii', errors='replace').strip()
                    assert line, 'Disconnected before emergency response'
                    transcript.append('< ' + line)
                    if 'halt' in line.lower() or 'kill()' in line:
                        break
                    assert time.monotonic() - started < 5, 'M112 delayed'
        assert process.wait(timeout=55) == 0, 'LCD process failed'
        assert snapshot.exists() and snapshot.stat().st_size > 1000, 'LCD snapshot missing'
        print('PASS: M115, temperatures, checksum/Resend, homing, XYZ, heaters, fan, LCD message, reconnect, M112 during M109, clean exit.')
    finally:
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=10)
        workspace.cleanup()
        (ARTIFACTS / 'lcd-smoke.log').write_text('\n'.join(transcript) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
