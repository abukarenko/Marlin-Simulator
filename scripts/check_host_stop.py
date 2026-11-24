"""Cold integration trial against a dedicated running simulator (--port PORT).

Never point this script at a physical printer. It homes and moves virtual axes.
"""
from pathlib import Path
import argparse
import re
import select
import socket
import time

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port',type=int,default=18199)
    args=parser.parse_args()
    root=Path(__file__).resolve().parents[1]
    (root/'artifacts').mkdir(exist_ok=True)
    log=[]
    with socket.create_connection(('127.0.0.1',args.port),timeout=10) as sock:
        pending=bytearray()
        def send(code):
            log.append('> '+code);sock.sendall((code+'\n').encode('ascii'))
        def line(timeout=30):
            deadline=time.monotonic()+timeout
            while b'\n' not in pending:
                remaining=deadline-time.monotonic()
                if remaining<=0:raise TimeoutError('Firmware response')
                sock.settimeout(remaining)
                data=sock.recv(4096)
                if not data:raise RuntimeError('Firmware disconnected')
                pending.extend(data)
            raw,_,rest=pending.partition(b'\n');pending[:]=rest
            text=raw.decode('ascii',errors='replace').strip();log.append('< '+text)
            if text.startswith(('Error:','!!')):raise RuntimeError(text)
            return text
        def command(code):
            send(code);reply=[]
            while True:
                text=line();reply.append(text)
                if text=='ok' or text.startswith('ok '):return '\n'.join(reply)
        def stop():
            started=time.monotonic();send('M411')
            while True:
                text=line()
                if text.startswith('HOST_STOP_DONE V1 '):break
                assert time.monotonic()-started<15,'Delayed STOP'
            # The interrupted G-code may emit its final ok independently.
            pending.clear()
            while select.select([sock],[],[],.3)[0]:sock.recv(4096)
            print(text,flush=True)
            return text
        def xyz():
            response=command('M114')
            result={}
            for k,v in re.findall(r'\b([XYZE]):\s*([-\d.]+)',response):result.setdefault(k,float(v))
            return result

        assert 'Cap:HOST_STOP:1' in command('M115')
        assert 'LIFT 0.000' in stop(),'Unhomed Z moved'
        command('G28');command('G90');command('G1 Z5 F600')
        command('G1 X40 Y60 F3000');command('M400')
        before=xyz()
        command('M410');command('M400')
        assert abs(xyz()['Z']-before['Z'])<.01,'M410 changed Z'
        for i in range(8):command(f'G1 X{150 if i%2==0 else 40} Y60 F300')
        time.sleep(1)
        assert 'LIFT 15.000' in stop()
        command('M400');after=xyz()
        assert 40.1<after['X']<100,after
        assert abs(after['Y']-60)<.01 and abs(after['Z']-20)<.02,after
        assert after['E']==before['E'],after
        time.sleep(1);stable=xyz()
        assert abs(stable['X']-after['X'])<.01,'Old queue replayed'
        print('PASS moving STOP: mid-move X, +15 Z, stable XY, E unchanged; M410 preserved',flush=True)
        send('M109 S240')
        while not line().startswith('T:'):pass
        assert 'LIFT 15.000' in stop()
        temperature=command('M105')
        targets=re.findall(r'/\s*([-\d.]+)',temperature)
        assert len(targets)>=2 and all(float(t)==0 for t in targets),temperature
        print('PASS M411 interrupts heat wait and disables heaters',flush=True)
        command('G1 X45 Y65 F3000');command('M400')
        assert abs(xyz()['X']-45)<.02,'Cannot resume new manual commands after STOP'
        print('PASS fresh commands after STOP',flush=True)
    (root/'artifacts/host-stop-smoke.log').write_text('\n'.join(log)+'\n',encoding='utf-8')

if __name__=='__main__':main()
