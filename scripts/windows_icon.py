"""Link the simulator's Windows Explorer icon into the native executable."""
Import('env')
from pathlib import Path
import subprocess

root = Path(env.subst('$PROJECT_DIR')).resolve().parents[1]
icon = root / 'assets/simulator.ico'

def compile_icon(target, source, env):
    output = Path(str(target[0]))
    resource = output.with_suffix('.rc')
    content = '1 ICON "' + icon.as_posix() + '"\n'
    if env.subst('$PIOENV') == 'lcd_windows':
        content += '''1 VERSIONINFO
FILEVERSION 1,0,0,0
PRODUCTVERSION 1,0,0,0
FILEOS 0x40004L
FILETYPE 0x1L
BEGIN
  BLOCK "StringFileInfo"
  BEGIN
    BLOCK "040904b0"
    BEGIN
      VALUE "FileDescription", "Easy Marlin Emulator"
      VALUE "FileVersion", "1.0.0"
      VALUE "ProductName", "Easy Marlin Emulator"
      VALUE "ProductVersion", "1.0.0"
      VALUE "OriginalFilename", "EasyMarlinEmulator.exe"
      VALUE "LegalCopyright", "GPL-3.0-or-later; Marlin and MarlinSimUI contributors"
    END
  END
  BLOCK "VarFileInfo"
  BEGIN
    VALUE "Translation", 0x409, 1200
  END
END
'''
    resource.write_text(content)
    subprocess.run([str(root / '.tools/mingw64/bin/windres.exe'), '-i', str(resource), '-o', str(output)], check=True)

obj = env.Command('$BUILD_DIR/simulator_icon.o', str(icon), compile_icon)
env.Append(LINKFLAGS=[obj[0]])
env.Depends('$BUILD_DIR/${PROGNAME}.exe', obj)
