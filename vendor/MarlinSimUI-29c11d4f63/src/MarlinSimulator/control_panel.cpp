#include "application.h"
#include "hardware/EndStop.h"
#include <algorithm>
#include <sstream>

namespace {
constexpr const char* settings_path = "simulator.settings";
std::string number(float value) { std::ostringstream text; text << value; return text.str(); }
std::string endstop_name(char axis, bool maximum) { return std::string("Endstop(") + axis + (maximum ? " Max)" : " Min)"); }
}

void Application::send_command(const std::string& command) {
  auto monitor = UserInterface::getElement<SerialMonitor>("Monitor");
  if (!monitor) return;
  auto line = command + "\n";
  if (monitor->serial_buffer.out.free() >= line.size()) {
    monitor->serial_buffer.out.write(reinterpret_cast<const uint8_t*>(line.data()), line.size());
    monitor->insert_text(("> " + line).c_str());
  }
}

void Application::draw_controls() {
  auto ticks = Kernel::SimulationRuntime::nanos();
  const auto now = std::chrono::steady_clock::now();
  const double elapsed = std::chrono::duration<double>(now - last_measure).count();
  if (elapsed >= 1) {
    measured_speed = (ticks - measured_ticks) / 1e9 / elapsed;
    measured_ticks = ticks; last_measure = now;
  }
  const auto seconds = ticks / 1000000000;
  ImGui::Text("%02llu:%02llu:%02llu   Actual %.2fx", static_cast<unsigned long long>(seconds / 3600),
              static_cast<unsigned long long>(seconds / 60 % 60), static_cast<unsigned long long>(seconds % 60), measured_speed);
  float speed = paused ? resume_speed : Kernel::TimeControl::realtime_scale.load();
  if (ImGui::SliderFloat("Speed", &speed, .1f, 20.0f, "%.2fx", ImGuiSliderFlags_Logarithmic)) {
    resume_speed = speed; if (!paused) Kernel::TimeControl::realtime_scale = speed;
  }
  if (ImGui::Button(paused ? "Resume" : "Pause")) {
    if (!paused) resume_speed = Kernel::TimeControl::realtime_scale;
    paused = !paused; Kernel::TimeControl::realtime_scale = paused ? 0 : resume_speed;
  }
  ImGui::SameLine();
  if (ImGui::Button("Realtime")) { paused = false; resume_speed = 1; Kernel::TimeControl::realtime_scale = 1; }
  ImGui::SameLine();
  if (ImGui::Button("Max")) { paused = false; resume_speed = 100; Kernel::TimeControl::realtime_scale = 100; }

  ImGui::SeparatorText("Temperature");
  auto temperature = [&](const char* label, const std::string& component, float& preset, bool bed, int tool) {
    auto heater = sim.testPrinter.get_component<Heater>(component);
    if (!heater) return;
    ImGui::PushID(label);
    const float current = static_cast<float>(heater->hotend_temperature.load());
    ImGui::Text("%s   %.1f C", label, current);
    ImGui::ProgressBar(std::clamp(current / (bed ? 120.f : 300.f), 0.f, 1.f), ImVec2(-1, 8), "");
    ImGui::SetNextItemWidth(80);
    ImGui::InputFloat("##Target", &preset, 0, 0, "%.0f C");
    preset = std::clamp(preset, 0.f, bed ? 110.f : 260.f);
    ImGui::SameLine();
    if (ImGui::Button("Set")) send_command((bed ? "M140 S" : "M104 T" + std::to_string(tool) + " S") + number(preset));
    ImGui::SameLine();
    if (ImGui::Button("Off")) send_command(bed ? "M140 S0" : "M104 T" + std::to_string(tool) + " S0");
    ImGui::PopID();
  };
  for (int i = 0; i < HOTENDS; ++i) temperature(("Extruder " + std::to_string(i + 1)).c_str(), "Hotend" + std::to_string(i) + " Heater", temperature_presets[i], false, i);
  temperature("Bed", "Bed Heater", bed_preset, true, 0);
  if (ImGui::Button("All heaters off")) {
    for (int i = 0; i < HOTENDS; ++i) send_command("M104 T" + std::to_string(i) + " S0");
    send_command("M140 S0");
  }

  ImGui::SeparatorText("Limit switches");
  if (ImGui::BeginTable("Limits", 2, ImGuiTableFlags_SizingStretchSame)) {
    for (char axis : {'X', 'Y', 'Z'}) {
      for (bool maximum : {false, true}) {
        ImGui::TableNextColumn();
        auto sensor = sim.testPrinter.get_component<EndStop>(endstop_name(axis, maximum));
        if (!sensor) { ImGui::TextDisabled("Unavailable"); continue; }
        const std::string label = std::string(1, axis) + (maximum ? "+" : "-") + (sensor->is_triggered() ? "  HIT" : "  open");
        const bool triggered = sensor->is_triggered();
        if (triggered) ImGui::PushStyleColor(ImGuiCol_Button, ImGui::GetStyleColorVec4(ImGuiCol_ButtonActive));
        ImGui::Button(label.c_str(), ImVec2(-1, 28));
        const bool held = ImGui::IsItemActive();
        if (triggered) ImGui::PopStyleColor();
        sensor->set_forced(held);
      }
    }
    ImGui::EndTable();
  }
  ImGui::TextDisabled("Hold a switch to simulate contact");

  ImGui::SeparatorText("Jog");
  auto jog = [&](char axis, float sign) {
    send_command("G91\nG1 " + std::string(1, axis) + number(sign * jog_step) + " F" + number(jog_feed) + "\nG90");
  };
  const float button = std::max(42.f, (ImGui::GetContentRegionAvail().x - 28) / 4);
  ImGui::Dummy(ImVec2(button + 4, 0)); ImGui::SameLine();
  if (ImGui::Button("Y+", ImVec2(button,32))) jog('Y',1);
  ImGui::SameLine(); ImGui::Dummy(ImVec2(button,0)); ImGui::SameLine();
  if (ImGui::Button("Z+", ImVec2(button,32))) jog('Z',1);
  if (ImGui::Button("X-", ImVec2(button,32))) jog('X',-1);
  ImGui::SameLine(); if (ImGui::Button("Home", ImVec2(button,32))) send_command("G28");
  ImGui::SameLine(); if (ImGui::Button("X+", ImVec2(button,32))) jog('X',1);
  ImGui::Dummy(ImVec2(button + 4,0)); ImGui::SameLine();
  if (ImGui::Button("Y-", ImVec2(button,32))) jog('Y',-1);
  ImGui::SameLine(); ImGui::Dummy(ImVec2(button,0)); ImGui::SameLine();
  if (ImGui::Button("Z-", ImVec2(button,32))) jog('Z',-1);
  ImGui::SetNextItemWidth(100); ImGui::InputFloat("Step, mm", &jog_step, .1f, 1, "%.2f");
  ImGui::SetNextItemWidth(100); ImGui::InputFloat("Feed, mm/min", &jog_feed, 100, 500, "%.0f");
  jog_step = std::clamp(jog_step, .01f, 100.f); jog_feed = std::clamp(jog_feed, 1.f, 10000.f);
  if (ImGui::Button("Stop motion")) send_command("M410");
  ImGui::SameLine(); if (ImGui::Button("Emergency stop")) send_command("M112");
}

void Application::reset_layout() {
  auto host = ImGui::FindWindowByName("DockSpaceWindwow");
  if (!host) return;
  const auto id = host->GetID("DockSpace");
  ImGui::DockBuilderRemoveNode(id);
  ImGui::DockBuilderAddNode(id, ImGuiDockNodeFlags_DockSpace);
  ImGui::DockBuilderSetNodeSize(id, ImGui::GetMainViewport()->WorkSize);
  ImGuiID main = id, left, bottom;
  left = ImGui::DockBuilderSplitNode(main, ImGuiDir_Left, .28f, nullptr, &main);
  bottom = ImGui::DockBuilderSplitNode(main, ImGuiDir_Down, .27f, nullptr, &main);
  ImGui::DockBuilderDockWindow("Simulation", left);
  ImGui::DockBuilderDockWindow("Viewport", main);
  ImGui::DockBuilderDockWindow("Monitor", bottom);
  ImGui::DockBuilderFinish(id);
  layout_pending = false;
}

void Application::load_preferences() {
  preferences.load(settings_path);
  layout_pending = preferences.get("version", 0) != 2;
  resume_speed = std::clamp(preferences.get("speed", 1.f), .1f, 100.f);
  Kernel::TimeControl::realtime_scale = resume_speed;
  jog_step = std::clamp(preferences.get("jog_step", 10.f), .01f, 100.f);
  jog_feed = std::clamp(preferences.get("jog_feed", 2000.f), 1.f, 10000.f);
  bed_preset = std::clamp(preferences.get("bed_target", 60.f), 0.f, 110.f);
  for (int i = 0; i < HOTENDS; ++i) temperature_presets[i] = std::clamp(preferences.get("target" + std::to_string(i), 200.f), 0.f, 260.f);
  auto& view = sim.vis;
  for (int i = 0; i < 3; ++i) {
    view.camera.position[i] = preferences.get("camera_position" + std::to_string(i), view.camera.position[i]);
    view.camera.rotation[i] = preferences.get("camera_rotation" + std::to_string(i), view.camera.rotation[i]);
  }
  view.camera.update_view();
  view.follow_mode = static_cast<FollowMode>(std::clamp(preferences.get("follow_mode",0),0,2));
  for(int i=0;i<3;++i) view.follow_offset[i] = preferences.get("follow_offset"+std::to_string(i),0.f);
  view.extrude_width = std::clamp(preferences.get("extrude_width", .4f), .01f, 10.f);
  view.extrude_thickness = std::clamp(preferences.get("extrude_thickness", .3f), .01f, 10.f);
  view.render_full_path = preferences.get("render_full_path", true);
  view.render_path_line = preferences.get("render_path_line", false);
  view.m_config.extrusion_check_min_line_length = preferences.get("path_check", .5);
  view.m_config.extrusion_check_max_vertical_deviation = preferences.get("path_vertical", .01);
  view.m_config.extrusion_segment_minimum_length = preferences.get("path_segment", .5);
  view.m_config.extrusion_segment_collinearity_max_deviation = preferences.get("path_collinear", .001);
  for (size_t i=0;i<view.extrusion.size();++i) view.extrusion[i].is_visible = preferences.get("extrusion_visible" + std::to_string(i), true);
  auto bed = sim.testPrinter.get_component<PrintBed>("Print Bed");
  for (int i=0;i<3;++i) bed->bed_level_points[i].z = std::clamp(preferences.get("bed_level" + std::to_string(i), 0.f), -5.f, 5.f);
  bed->bed_shape = static_cast<PrintBed::bed_shape_t>(std::clamp(preferences.get("bed_shape", 0), 0, 6));
  bed->bed_shape_amplitude = std::clamp(preferences.get("bed_amplitude", 1.f), -5.f, 5.f);
  bed->gradient_enabled = preferences.get("bed_gradient", false);
  bed->build_3point(bed->bed_level_points[0], bed->bed_level_points[1], bed->bed_level_points[2]); bed->dirty = true;
  for (char axis : {'X','Y','Z'}) for (bool maximum : {false,true}) {
    auto sensor = sim.testPrinter.get_component<EndStop>(endstop_name(axis,maximum));
    if (sensor) sensor->set_enabled(preferences.get(endstop_name(axis,maximum), true));
  }
  for (const auto& panel : user_interface.ui_elements) {
    const bool default_active = panel.first == "Simulation" || panel.first == "Viewport" || panel.first == "Monitor";
    panel.second->active = preferences.get("panel_" + panel.first, default_active);
  }
  for (int i=0;i<HOTENDS;++i) {
    auto heater = sim.testPrinter.get_component<Heater>("Hotend" + std::to_string(i) + " Heater");
    heater->m_temperature_sensor_mode = static_cast<Heater::temperature_sensor_mode>(std::clamp(preferences.get("sensor" + std::to_string(i), 0),0,3));
  }
  auto heater = sim.testPrinter.get_component<Heater>("Bed Heater");
  if (heater) heater->m_temperature_sensor_mode = static_cast<Heater::temperature_sensor_mode>(std::clamp(preferences.get("sensor_bed",0),0,3));
  auto window_handle = static_cast<SDL_Window*>(window.getHandle());
  SDL_SetWindowSize(window_handle, std::clamp(preferences.get("window_w",1280),800,7680), std::clamp(preferences.get("window_h",800),600,4320));
  if (preferences.values.count("window_x")) SDL_SetWindowPosition(window_handle, preferences.get("window_x",100),preferences.get("window_y",100));
  if (preferences.get("window_maximized",false)) SDL_MaximizeWindow(window_handle);
}

void Application::save_preferences(bool force) {
  const auto now = std::chrono::steady_clock::now();
  if (!force && now-last_save < std::chrono::milliseconds(500)) return;
  last_save = now;
  auto& p = preferences; auto& view = sim.vis;
  p.set("version",2); p.set("speed",paused?resume_speed:Kernel::TimeControl::realtime_scale.load());
  p.set("jog_step",jog_step); p.set("jog_feed",jog_feed); p.set("bed_target",bed_preset);
  for(int i=0;i<HOTENDS;++i) p.set("target"+std::to_string(i),temperature_presets[i]);
  for(int i=0;i<3;++i) { p.set("camera_position"+std::to_string(i),view.camera.position[i]); p.set("camera_rotation"+std::to_string(i),view.camera.rotation[i]); }
  p.set("extrude_width",view.extrude_width); p.set("extrude_thickness",view.extrude_thickness);
  p.set("render_full_path",view.render_full_path); p.set("render_path_line",view.render_path_line);
  p.set("follow_mode",int(view.follow_mode));
  for(int i=0;i<3;++i) p.set("follow_offset"+std::to_string(i),view.follow_offset[i]);
  p.set("path_check",view.m_config.extrusion_check_min_line_length); p.set("path_vertical",view.m_config.extrusion_check_max_vertical_deviation);
  p.set("path_segment",view.m_config.extrusion_segment_minimum_length); p.set("path_collinear",view.m_config.extrusion_segment_collinearity_max_deviation);
  for(size_t i=0;i<view.extrusion.size();++i) p.set("extrusion_visible"+std::to_string(i),view.extrusion[i].is_visible);
  auto bed=sim.testPrinter.get_component<PrintBed>("Print Bed");
  for(int i=0;i<3;++i) p.set("bed_level"+std::to_string(i),bed->bed_level_points[i].z);
  p.set("bed_shape",int(bed->bed_shape)); p.set("bed_amplitude",bed->bed_shape_amplitude); p.set("bed_gradient",bed->gradient_enabled);
  for(char axis:{'X','Y','Z'}) for(bool maximum:{false,true}) { auto sensor=sim.testPrinter.get_component<EndStop>(endstop_name(axis,maximum)); if(sensor) p.set(endstop_name(axis,maximum),sensor->is_enabled()); }
  for(const auto& panel:user_interface.ui_elements) p.set("panel_"+panel.first,panel.second->active);
  for(int i=0;i<HOTENDS;++i) { auto heater=sim.testPrinter.get_component<Heater>("Hotend"+std::to_string(i)+" Heater"); p.set("sensor"+std::to_string(i),int(heater->m_temperature_sensor_mode.load())); }
  auto heater=sim.testPrinter.get_component<Heater>("Bed Heater"); if(heater) p.set("sensor_bed",int(heater->m_temperature_sensor_mode.load()));
  int x,y,w,h; auto handle=static_cast<SDL_Window*>(window.getHandle()); SDL_GetWindowPosition(handle,&x,&y); SDL_GetWindowSize(handle,&w,&h);
  p.set("window_x",x);p.set("window_y",y);p.set("window_w",w);p.set("window_h",h);p.set("window_maximized",bool(SDL_GetWindowFlags(handle)&SDL_WINDOW_MAXIMIZED));
  const auto text=p.serialize(); if(force||text!=last_preferences) { if(p.save(settings_path)) last_preferences=text; }
}

void Application::reset_defaults() {
  preferences.values.clear(); preferences.save(settings_path);
  sim.vis.camera.position={37,121,129};
  sim.vis.camera.look_at({build_plate_dimension.x*.5f,0,-build_plate_dimension.y*.5f});
  load_preferences(); paused=false; layout_pending=true;
  save_preferences(true);
}
