"""Verify checksummed input and M112 during M109. Leaves the simulator halted."""
from pathlib import Path
import argparse
import socket
import time

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--port', type=int, default=8099)
args = parser.parse_args()
lines = []
with socket.create_connection(('127.0.0.1', args.port), timeout=10) as connection:
    connection.settimeout(10)
    reader = connection.makefile('rb')
    def read():
        raw = reader.readline()
        if not raw:
            raise RuntimeError('Disconnected before emergency response')
        line = raw.decode('ascii', errors='replace').strip()
        lines.append('< ' + line)
        return line
    def send(command):
        lines.append('> ' + command)
        connection.sendall((command + '\n').encode('ascii'))
    def acknowledged(command):
        send(command)
        while True:
            line = read()
            if line.startswith('Error:') or line.startswith('Resend:'):
                raise RuntimeError(line)
            if line == 'ok' or line.startswith('ok '):
                return
    acknowledged('M110 N0')
    numbered = 'N1 M105'
    checksum = 0
    for byte in numbered.encode('ascii'):
        checksum ^= byte
    acknowledged(f'{numbered}*{checksum}')
    acknowledged('M110 N0')
    # Check actual extrusion without waiting for printing temperature in this test.
    acknowledged('M302 P1')
    acknowledged('M83')
    acknowledged('G92 E0')
    acknowledged('G1 E1 F120')
    acknowledged('M400')
    acknowledged('M114')
    assert any('E:1.00' in line for line in lines), 'Extrusion position not updated'
    acknowledged('M302 P0')
    send('M109 S200')
    while not read().startswith('T:'):
        pass
    started = time.monotonic()
    send('M112')
    while True:
        line = read()
        if 'halt' in line.lower() or 'kill()' in line:
            break
        assert time.monotonic() - started < 10, 'Emergency stop was delayed'
    elapsed = time.monotonic() - started
    assert elapsed < 10, f'Emergency stop took {elapsed:.2f}s'
    (root / 'artifacts/emergency-smoke.log').write_text('\n'.join(lines) + '\n', encoding='utf-8')
    print(f'PASS: checksum, E motion, M112 during M109 ({elapsed:.3f}s).')
