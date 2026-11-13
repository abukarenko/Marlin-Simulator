"""Link the simulator's Windows Explorer icon into the native executable."""
Import('env')
from pathlib import Path
import subprocess

root = Path(env.subst('$PROJECT_DIR')).resolve().parents[1]
icon = root / 'assets/simulator.ico'

def compile_icon(target, source, env):
    output = Path(str(target[0]))
    resource = output.with_suffix('.rc')
    resource.write_text('1 ICON "' + icon.as_posix() + '"\n')
    subprocess.run([str(root / '.tools/mingw64/bin/windres.exe'), '-i', str(resource), '-o', str(output)], check=True)

obj = env.Command('$BUILD_DIR/simulator_icon.o', str(icon), compile_icon)
env.Append(LINKFLAGS=[obj[0]])
env.Depends('$BUILD_DIR/${PROGNAME}.exe', obj)
