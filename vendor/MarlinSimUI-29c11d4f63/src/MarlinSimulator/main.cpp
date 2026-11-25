#include "RawSocketSerial.h"
#include <thread>
#include <atomic>

#include "application.h"
#include "execution_control.h"

#include "src/inc/MarlinConfig.h"

#include "audio.h"
#include "host_viewer.h"

RawSocketSerial net_serial{};

std::atomic_bool main_finished = false;

void HAL_idletask() {
  Kernel::yield();
}

extern void setup();
extern void loop();
void marlin_loop() {
  static bool initialised = false;
  if (!initialised) {
    initialised = true;
    #ifdef MYSERIAL0
      MYSERIAL0.begin(BAUDRATE);
      SERIAL_FLUSHTX();
    #endif
    HAL_timer_init();
    setup();
  } else loop();
}

void simulation_main() {
  #ifdef __APPLE__
    pthread_setname_np("simulation_main");
  #else
    pthread_setname_np(pthread_self(), "simulation_main");
  #endif

  // Marlin Loop 500hz
  Kernel::Timers::timerInit(3, 1000000);
  Kernel::Timers::timerStart(3, 500);
  Kernel::Timers::timerEnable(3);
  Kernel::is_initialized(true);

  while(!main_finished) {
    try {
      Kernel::execute_loop();
    } catch (std::runtime_error& e) {
      // stack unrolled by exception in order to exit cleanly
      // todo: use a custom exception
      printf("Exception: %s\n", e.what());
      printf("Marlin thread terminated\n");
      main_finished = true;
    }
  }
}

#include "LcdEmulator.h"

// Main code
int main(int argc, char** argv) {
  setvbuf(stdout, nullptr, _IONBF, 0);
  setvbuf(stderr, nullptr, _IONBF, 0);
  SetUnhandledExceptionFilter([](EXCEPTION_POINTERS* error) -> LONG {
    fprintf(stderr, "Native exception: %08lx at %p\n", error->ExceptionRecord->ExceptionCode, error->ExceptionRecord->ExceptionAddress);
    void* frames[48]; const auto count = CaptureStackBackTrace(0,48,frames,nullptr);
    fprintf(stderr, "Image base: %p\n", GetModuleHandle(nullptr));
    for (unsigned i=0;i<count;++i) fprintf(stderr,"Frame: %p\n",frames[i]);
    return EXCEPTION_EXECUTE_HANDLER;
  });
  #ifdef MARLIN_LCD_EMULATOR
    return run_lcd_emulator(argc, argv);
  #endif
  uint16_t port = 8099;
  double quit_after = 0;
  std::string snapshot;
  bool reset = false;
  uintptr_t viewer_parent = 0;
  for (int i = 1; i < argc; ++i) {
    const std::string arg = argv[i];
    if (arg == "--port" && i + 1 < argc) { const int value = std::stoi(argv[++i]); if(value < 1 || value > 65535) return 2; port = value; }
    else if (arg == "--quit-after" && i + 1 < argc) quit_after = std::stod(argv[++i]);
    else if (arg == "--snapshot" && i + 1 < argc) snapshot = argv[++i];
    else if (arg == "--reset-default") reset = true;
    else if (arg == "--host-viewer" && i + 1 < argc) viewer_parent = std::stoull(argv[++i]);
  }
  if (viewer_parent) return run_host_viewer(viewer_parent);
  bool audio_enabled = true; // TODO: get from config
  uint32_t sdl_flags = audio_enabled ? SDL_INIT_AUDIO : 0;
  SDL_Init(sdl_flags);
  SDLNet_Init();

  if (audio_enabled) audio_init();

  // Listen before starting simulator loop to avoid
  // thread synchronization issues if listen_on_port fails
  net_serial.listen_on_port(port);

  {
  Application app;
  if (reset) app.reset_defaults();
  std::thread simulation_loop(simulation_main);
  const auto started = std::chrono::steady_clock::now();

  while (app.active) {
    app.update();
    app.render();
    const double elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
    if (!snapshot.empty() && elapsed > 1.0) {
      int width, height; SDL_GL_GetDrawableSize(static_cast<SDL_Window*>(app.window.getHandle()), &width, &height);
      std::vector<uint8_t> pixels(width * height * 4), flipped(pixels.size());
      glReadBuffer(GL_FRONT); glReadPixels(0,0,width,height,GL_RGBA,GL_UNSIGNED_BYTE,pixels.data());
      for(int y=0;y<height;++y) std::copy_n(pixels.data()+(height-1-y)*width*4,width*4,flipped.data()+y*width*4);
      auto surface=SDL_CreateRGBSurfaceWithFormatFrom(flipped.data(),width,height,32,width*4,SDL_PIXELFORMAT_RGBA32);
      if(surface) { SDL_SaveBMP(surface,snapshot.c_str()); SDL_FreeSurface(surface); }
      snapshot.clear();
    }
    if (quit_after > 0 && elapsed >= quit_after) app.active = false;
    std::this_thread::yield();
  }

  main_finished = true;
  Kernel::quit_requested = true;
  simulation_loop.join();
  net_serial.stop();

  }

  SDLNet_Quit();
  SDL_Quit();

  return 0;
}
