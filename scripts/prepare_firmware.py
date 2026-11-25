"""Configure the pinned Marlin release for a desktop Cartesian printer."""
from pathlib import Path
import re
import shutil

ROOT = Path(__file__).resolve().parents[1]
MARLIN = ROOT / 'vendor' / 'Marlin-2.1.2.9'

def main():
    config = MARLIN / 'Marlin' / 'Configuration.h'
    text = config.read_text(encoding='utf-8')
    settings = {
        'MOTHERBOARD': 'BOARD_SIMULATED', 'BAUDRATE': '115200', 'SERIAL_PORT_2': '3',
        'CUSTOM_MACHINE_NAME': 'MARLIN_DESKTOP_MACHINE_NAME',
        'X_BED_SIZE': '220', 'Y_BED_SIZE': '220', 'Z_MAX_POS': '250',
        'EEPROM_SETTINGS': '', 'EEPROM_AUTO_INIT': '',
        'USE_XMAX_PLUG': '', 'USE_YMAX_PLUG': '', 'USE_ZMAX_PLUG': '',
    }
    for key, value in settings.items():
        text, count = re.subn(r'^(\s*)(?://\s*)?#define\s+' + key + r'\b[^\n]*',
                             lambda m: m[1] + '#define ' + key + (' ' + value if value else ''), text, flags=re.M)
        if count != 1:
            raise RuntimeError(f'Expected one definition of {key}, found {count}')
    config.write_text(text, encoding='utf-8')
    # Dedicated LCD build; leave the original simulator profile unchanged.
    text = config.read_text(encoding='utf-8')
    marker = '// Desktop LCD emulator profile'
    profile = marker + '\n#ifdef MARLIN_LCD_EMULATOR\n  #define MARLIN_DESKTOP_MACHINE_NAME "Easy Marlin Emulator"\n  #define REPRAP_DISCOUNT_FULL_GRAPHIC_SMART_CONTROLLER\n  #define SET_PROGRESS_MANUALLY\n#else\n  #define MARLIN_DESKTOP_MACHINE_NAME "Marlin Emulator"\n#endif\n'
    if marker in text:
        text = re.sub(re.escape(marker) + r'\n#ifdef MARLIN_LCD_EMULATOR\n.*?\n#endif\n', lambda _: profile, text, flags=re.S)
    else:
        text += '\n' + profile
    config.write_text(text, encoding='utf-8')
    advanced = MARLIN / 'Marlin' / 'Configuration_adv.h'
    text = advanced.read_text(encoding='utf-8')
    text = re.sub(r'^(\s*)//#define EMERGENCY_PARSER\b', r'\1#define EMERGENCY_PARSER', text, flags=re.M)
    text = re.sub(r'^(\s*)//#define M114_REALTIME\b', r'\1#define M114_REALTIME', text, flags=re.M)
    advanced.write_text(text, encoding='utf-8')
    (ROOT / 'config').mkdir(exist_ok=True)
    for name in ('Configuration.h', 'Configuration_adv.h'):
        shutil.copy2(MARLIN / 'Marlin' / name, ROOT / 'config' / name)

    # Use local pinned libraries and private toolchain, independent of C:\\msys64.
    ini = MARLIN / 'ini' / 'native.ini'
    text = ini.read_text(encoding='utf-8')
    platform = (ROOT / '.tools' / 'platform-native-1.2.1').as_posix()
    text = re.sub(r'^platform\s*=.*$', 'platform = file://' + platform, text, flags=re.M)
    for name, directory in [('MarlinSimUI', 'MarlinSimUI-29c11d4f63'),
                            ('Adafruit NeoPixel', 'Adafruit_NeoPixel-c6b319f447'),
                            ('LiquidCrystal', 'LiquidCrystal-322fb5fc23')]:
        text = re.sub(re.escape(name) + r'=[^\n]+',
                      name + '=symlink://' + (ROOT / 'vendor' / directory).as_posix(), text)
    toolchain = (ROOT / '.tools' / 'mingw64').as_posix()
    text = re.sub(r'-IC:[^\s]+SDL2', lambda _: f'-I"{toolchain}/include/SDL2" -L"{toolchain}/lib"', text)
    text = re.sub(r'-I"[^"]*/include/SDL2" -L"[^"]*/lib"',
                  lambda _: f'-I"{toolchain}/include/SDL2" -L"{toolchain}/lib"', text)
    # A checkout may live at a different path from the original workstation.
    for directory in ('MarlinSimUI-29c11d4f63', 'Adafruit_NeoPixel-c6b319f447', 'LiquidCrystal-322fb5fc23'):
        text = re.sub(r'symlink://[^\n]*?/vendor/' + re.escape(directory),
                      lambda _: 'symlink://' + (ROOT / 'vendor' / directory).as_posix(), text)
    # Current MinGW uses stack protection in libstdc++ without a separate libssp.
    text = text.replace(' -lssp', '')
    if '-lws2_32' not in text:
        text = text.replace('-lSDL2_net -lopengl32', '-lSDL2_net -lws2_32 -lopengl32')
    ini.write_text(text, encoding='utf-8')

    text = ini.read_text(encoding='utf-8')
    if '[env:lcd_windows]' not in text:
        text += '\n[env:lcd_windows]\nextends = env:simulator_windows\nbuild_flags = ${env:simulator_windows.build_flags} -DMARLIN_LCD_EMULATOR\n'
        ini.write_text(text, encoding='utf-8')

    # Bind the emulator endpoint only to this computer.
    socket = ROOT / 'vendor/MarlinSimUI-29c11d4f63/src/MarlinSimulator/RawSocketSerial.h'
    shutil.copy2(ROOT / 'native/RawSocketSerialWindows.h', socket)
    # Windows headers must precede Arduino's INPUT and Marlin's ABC macros.
    for name in ('main.cpp', 'execution_control.cpp'):
        source = socket.parent / name
        text = source.read_text(encoding='utf-8')
        text = text.replace('#include "RawSocketSerial.h"\n', '')
        source.write_text('#include "RawSocketSerial.h"\n' + text, encoding='utf-8')
    shutil.copy2(ROOT / 'native/LcdEmulator.h', socket.parent / 'LcdEmulator.h')
    source = socket.parent / 'main.cpp'
    text = source.read_text(encoding='utf-8')
    if '#include "LcdEmulator.h"' not in text:
        text = text.replace('// Main code', '#include "LcdEmulator.h"\n\n// Main code')
        text = text.replace('  uint16_t port = 8099;', '''  #ifdef MARLIN_LCD_EMULATOR
    return run_lcd_emulator(argc, argv);
  #endif
  uint16_t port = 8099;''')
    source.write_text(text, encoding='utf-8')
    # Serialize LCD bus writes, rendering and physical encoder input.
    source = socket.parent / 'hardware/ST7920Device.h'
    text = source.read_text(encoding='utf-8')
    if 'state_mutex' not in text:
        text = text.replace('#include <list>', '#include <list>\n#include <mutex>')
        text = text.replace('  enum KeyName {', '  std::recursive_mutex state_mutex;\n  enum KeyName {')
    source.write_text(text, encoding='utf-8')
    source = socket.parent / 'hardware/ST7920Device.cpp'
    text = source.read_text(encoding='utf-8')
    for signature in ('void ST7920Device::process_command(Command cmd)', 'void ST7920Device::update()', 'void ST7920Device::interrupt(GpioEvent& ev)'):
        text = text.replace(signature + ' {\n', signature + ' {\n  std::scoped_lock lock(state_mutex);\n') if signature + ' {\n  std::scoped_lock lock(state_mutex);' not in text else text
    source.write_text(text, encoding='utf-8')
    source = socket.parent / 'execution_control.cpp'
    text = source.read_text(encoding='utf-8')
    text = text.replace('''  if (terminal_3) {
    serial_stream_3.transmit_buffer.read(terminal_3->serial_buffer.in);
    terminal_3->serial_buffer.out.read(serial_stream_3.receive_buffer);
  }''', '''  {
    uint8_t bytes[512];
    auto length = serial_stream_3.transmit_buffer.read(bytes, sizeof(bytes));
    if (length) {
      net_serial.write(bytes, length);
      if (terminal_3) terminal_3->serial_buffer.in.write(bytes, length);
    }
    if (terminal_3) terminal_3->serial_buffer.out.read(serial_stream_3.receive_buffer);
  }''')
    text = text.replace('''    auto count = net_serial.readBytes(buffer, 512);
    serial_stream_3.receive_buffer.write((uint8_t *)buffer, count);''', '''    auto count = net_serial.readBytes(buffer, std::min(size_t(512), serial_stream_3.receive_buffer.free()));
    #if ENABLED(EMERGENCY_PARSER)
      for (size_t i = 0; i < count; ++i)
        EmergencyParser::update(serial_stream_3.emergency_state, uint8_t(buffer[i]));
    #endif
    serial_stream_3.receive_buffer.write((uint8_t *)buffer, count);''')
    source.write_text(text, encoding='utf-8')
    hal = socket.parent / 'marlin_hal_impl/HAL.cpp'
    text = hal.read_text(encoding='utf-8').replace('MSerialT serial_stream_3(false);', 'MSerialT serial_stream_3(true);')
    hal.write_text(text, encoding='utf-8')
    print('Configured Marlin 2.1.2.9: Cartesian 220 x 220 x 250, one extruder, heated bed, EEPROM.')

if __name__ == '__main__':
    main()
