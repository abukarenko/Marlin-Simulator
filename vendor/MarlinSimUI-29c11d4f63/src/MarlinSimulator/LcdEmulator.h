// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once
#ifdef MARLIN_LCD_EMULATOR
#include <imgui_impl_sdl2.h>
#include <imgui_impl_opengl3.h>
#include "hardware/ST7920Device.h"

// Pause only the firmware thread; the LCD window keeps rendering the logo.
void easy_marlin_bootscreen_hold() {
  std::this_thread::sleep_for(std::chrono::seconds(1));
}

// Display the real ST7920 bus output. No G-code parser or telemetry polling.
int run_lcd_emulator(int argc, char** argv) {
  int port = 7000, scale = 4;
  double quit_after = 0;
  double snapshot_after = 3;
  std::string snapshot;
  try {
    for (int i = 1; i < argc; ++i) {
      const std::string arg = argv[i];
      if (arg == "--port" && i + 1 < argc) port = std::stoi(argv[++i]);
      else if (arg == "--scale" && i + 1 < argc) scale = std::stoi(argv[++i]);
      else if (arg == "--quit-after" && i + 1 < argc) quit_after = std::stod(argv[++i]);
      else if (arg == "--snapshot" && i + 1 < argc) snapshot = argv[++i];
      else if (arg == "--snapshot-after" && i + 1 < argc) snapshot_after = std::stod(argv[++i]);
      else throw std::runtime_error("Unknown or incomplete option: " + arg);
    }
    if (port < 1 || port > 65535 || scale < 1 || scale > 12) throw std::runtime_error("Port: 1..65535; scale: 1..12");
    SDL_Init(SDL_INIT_AUDIO);
    SDLNet_Init();
    net_serial.listen_on_port(uint16_t(port));
    audio_init();
    {
      Window window;
      window.init({.title = "Easy Marlin Emulator"});
      auto* handle = static_cast<SDL_Window*>(window.getHandle());
      if (!handle || !SDL_GL_GetCurrentContext() || !ImGui::GetCurrentContext())
        throw std::runtime_error("Unable to initialize the LCD graphics window");
      SDL_SetWindowMinimumSize(handle, 128, 64);
      SDL_SetWindowSize(handle, 128 * scale, 64 * scale);
      SDL_SetWindowTitle(handle, ("Easy Marlin Emulator 1.1.0 | T0+T1 / BLTouch | 127.0.0.1:" + std::to_string(port) + " | Wheel / Enter: encoder | F1: help").c_str());
      ImGui::GetIO().IniFilename = nullptr;
      VirtualPrinter::on_kinematic_update = [](kinematic_state&) {};
      VirtualPrinter::build();
      auto lcd = VirtualPrinter::get_component<ST7920Device>("ST7920Device Display");
      if (!lcd) throw std::runtime_error("ST7920 display is missing");
      lcd->foreground_color = {175, 211, 232};
      lcd->background_color = {20, 18, 145};
      std::thread simulation(simulation_main);
      const auto started = std::chrono::steady_clock::now();
      bool active = true;
      int encoder_steps = 0;
      bool held[ST7920Device::COUNT] = {};
      uint64_t pressed_until[ST7920Device::COUNT] = {};
      auto button = [&](ST7920Device::KeyName key, bool down) {
        held[key] = down;
        if (down) pressed_until[key] = SDL_GetTicks64() + 120;
      };
      while (active && !main_finished) {
        SDL_Event event;
        while (SDL_PollEvent(&event)) {
          std::scoped_lock lock(lcd->state_mutex);
          if (event.type == SDL_QUIT) active = false;
          if (event.type == SDL_WINDOWEVENT) {
            if (event.window.event == SDL_WINDOWEVENT_CLOSE) active = false;
            if (event.window.event == SDL_WINDOWEVENT_FOCUS_LOST)
              for (int key = 0; key < ST7920Device::COUNT; ++key) { held[key] = false; pressed_until[key] = 0; }
            if (event.window.event == SDL_WINDOWEVENT_RESIZED) {
              scale = std::clamp(int(std::lround(event.window.data1 / 128.0)), 1, 12);
              if (event.window.data1 != scale * 128 || event.window.data2 != scale * 64)
                SDL_SetWindowSize(handle, scale * 128, scale * 64);
            }
          }
          if (event.type == SDL_MOUSEWHEEL) encoder_steps += event.wheel.y > 0 ? -4 : event.wheel.y < 0 ? 4 : 0;
          if (event.type == SDL_MOUSEBUTTONDOWN || event.type == SDL_MOUSEBUTTONUP)
            if (event.button.button == SDL_BUTTON_LEFT) button(ST7920Device::ENCODER_BUTTON, event.type == SDL_MOUSEBUTTONDOWN);
          if (event.type == SDL_KEYDOWN || event.type == SDL_KEYUP) {
            bool down = event.type == SDL_KEYDOWN;
            switch (event.key.keysym.sym) {
              case SDLK_RETURN: case SDLK_SPACE: button(ST7920Device::ENCODER_BUTTON, down); break;
              case SDLK_ESCAPE: case SDLK_LEFT: button(ST7920Device::BACK_BUTTON, down); break;
              case SDLK_k: button(ST7920Device::KILL_BUTTON, down); break;
              case SDLK_UP: if (down) encoder_steps -= 4; break;
              case SDLK_DOWN: if (down) encoder_steps += 4; break;
              case SDLK_F1: if (down) SDL_ShowSimpleMessageBox(SDL_MESSAGEBOX_INFORMATION, "Easy Marlin Emulator", "TCP: 127.0.0.1 (port in title)\nWheel / Up / Down: rotate encoder\nClick / Enter / Space: press encoder\nEsc / Left: back (if supported by firmware)\nK: emergency kill\nResize: integer LCD scale 1x..12x\nRestart application to reset firmware", handle); break;
            }
          }
        }
        {
          std::scoped_lock lock(lcd->state_mutex);
          for (int key = 0; key < ST7920Device::COUNT; ++key)
            lcd->key_pressed[key] = held[key] || SDL_GetTicks64() < pressed_until[key];
          if (encoder_steps) { lcd->encoder_position += encoder_steps > 0 ? 1 : -1; encoder_steps += encoder_steps > 0 ? -1 : 1; }
        }
        VirtualPrinter::update();
        ImGui_ImplOpenGL3_NewFrame();
        ImGui_ImplSDL2_NewFrame();
        ImGui::NewFrame();
        const auto size = ImGui::GetIO().DisplaySize;
        ImGui::GetBackgroundDrawList()->AddImage((ImTextureID)(intptr_t)lcd->texture_id, ImVec2(0, 0), size);
        ImGui::Render();
        int w, h; SDL_GL_GetDrawableSize(handle, &w, &h);
        glViewport(0, 0, w, h);
        glClear(GL_COLOR_BUFFER_BIT);
        ImGui_ImplOpenGL3_RenderDrawData(ImGui::GetDrawData());
        window.swap_buffers();
        const double elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
        if (!snapshot.empty() && elapsed > snapshot_after) {
          auto surface = SDL_CreateRGBSurfaceWithFormatFrom(lcd->texture_data, 128, 64, 24, 128 * 3, SDL_PIXELFORMAT_RGB24);
          if (surface) { SDL_SaveBMP(surface, snapshot.c_str()); SDL_FreeSurface(surface); }
          snapshot.clear();
        }
        if (quit_after > 0 && elapsed >= quit_after) active = false;
        SDL_Delay(10);
      }
      main_finished = true;
      Kernel::quit_requested = true;
      simulation.join();
      net_serial.stop();
    }
    SDLNet_Quit();
    return 0;
  } catch (const std::exception& error) {
    SDL_ShowSimpleMessageBox(SDL_MESSAGEBOX_ERROR, "Easy Marlin Emulator", error.what(), nullptr);
    return 1;
  }
}
#endif
