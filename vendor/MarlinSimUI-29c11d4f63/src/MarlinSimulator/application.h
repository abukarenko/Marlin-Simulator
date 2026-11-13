#pragma once

#include <fstream>

#include "window.h"
#include "user_interface.h"

#include "hardware/Heater.h"
#include "hardware/print_bed.h"

#include "visualisation.h"

#include <string>
#include <imgui.h>
#include <implot.h>
#include "user_interface.h"

#include "virtual_printer.h"
#include "preferences.h"

class Simulation {
public:

  Simulation() : vis(testPrinter) {}

  void init() {
    testPrinter.build();
    vis.create();
  }

  void process_event(SDL_Event& e) {}

  void update() {
    testPrinter.update();
  }

  void ui_callback(UiWindow*) {

  }

  void ui_info_callback(UiWindow *w) {
    vis.ui_info_callback(w);
  }

  VirtualPrinter testPrinter;
  Visualisation vis;
};

class Application {
public:
  Application();
  ~Application();

  void update();
  void render();
  void draw_controls();
  void load_preferences();
  void save_preferences(bool force = false);
  void reset_defaults();
  void reset_layout();
  void send_command(const std::string& command);

  Preferences preferences;
  std::string last_preferences;
  float temperature_presets[8] = {200,200,200,200,200,200,200,200};
  float bed_preset = 60, jog_step = 10, jog_feed = 2000, resume_speed = 1;
  bool paused = false, layout_pending = false;
  double measured_speed = 0;
  uint64_t measured_ticks = 0;
  std::chrono::steady_clock::time_point last_measure = std::chrono::steady_clock::now();
  std::chrono::steady_clock::time_point last_save = std::chrono::steady_clock::now();

  bool active = true;
  Window window;
  UserInterface user_interface;
  Simulation sim;
  ImVec4 clear_color = ImVec4(0.45f, 0.55f, 0.60f, 1.00f);
  std::ifstream input_file;
};
