"""Integration check against a running, freshly started MarlinSimulator."""
from pathlib import Path
import re
import socket
import time

ROOT = Path(__file__).resolve().parents[1]

def main():
    transcript = []
    (ROOT / 'artifacts').mkdir(exist_ok=True)
    log = ROOT / 'artifacts/firmware-smoke.log'
    log.write_text('', encoding='utf-8')
    def record(line):
        transcript.append(line)
        with log.open('a', encoding='utf-8') as output:
            output.write(line + '\n')
    with socket.create_connection(('127.0.0.1', 8099), timeout=10) as connection:
        connection.settimeout(1)
        stream = connection.makefile('rb')
        def command(code, timeout=120):
            record('> ' + code)
            print('>', code, flush=True)
            connection.sendall((code + '\n').encode('ascii'))
            lines = []
            deadline = time.monotonic() + timeout
            # A command timeout terminates this check and its connection.
            connection.settimeout(timeout)
            while time.monotonic() < deadline:
                raw = stream.readline()
                if not raw:
                    raise RuntimeError('Firmware disconnected')
                line = raw.decode('ascii', errors='replace').strip()
                lines.append(line)
                record('< ' + line)
                if line.startswith('Error:') or line.startswith('!!'):
                    raise RuntimeError(line)
                if line == 'ok' or line.startswith('ok '):
                    return '\n'.join(lines)
            raise TimeoutError(code)

        assert '2.1.2.9' in command('M115'), 'Incorrect firmware version'
        assert 'T:' in command('M105'), 'No thermistor report'
        command('G28')
        command('G90')
        command('G1 X30 Y40 Z5 F3000')
        command('M400')
        position = command('M114')
        for axis, expected in [('X', 30), ('Y', 40), ('Z', 5)]:
            match = re.search(r'\b' + axis + r':([\d.-]+)', position)
            assert match and abs(float(match[1]) - expected) < .01, position
        command('M104 S50')
        command('M109 S50', 180)
        temperature = command('M105')
        match = re.search(r'\bT:([\d.]+)', temperature)
        assert match and float(match[1]) >= 45, temperature
        command('M104 S0')
        command('M500')
        command('M501')
        command('M503')
        (ROOT / 'artifacts').mkdir(exist_ok=True)
        (ROOT / 'artifacts/firmware-smoke.log').write_text('\n'.join(transcript) + '\n', encoding='utf-8')
        print('PASS: real Marlin 2.1.2.9, homing, XYZ motion, hotend heating, EEPROM.', flush=True)

if __name__ == '__main__':
    main()
