#pragma once

#include <atomic>

#include <imgui.h>

#include "Gpio.h"
#include "../virtual_printer.h"

class EndStop : public VirtualPrinter::Component {
public:
  EndStop(pin_type endstop, bool invert_logic, std::function<bool()> triggered) : VirtualPrinter::Component("EndStop"), endstop(endstop), invert_logic(invert_logic), triggered(triggered) {
    Gpio::attach(endstop, [this](GpioEvent& ev){ this->interrupt(ev); });
  }
  ~EndStop() {}
  bool is_triggered() const { return last_contact.load(); }
  bool is_enabled() const { return enabled.load(); }
  void set_enabled(bool value) { enabled = value; }
  void set_forced(bool value) { forced = value; }

  void ui_widget() {
    bool value = is_triggered();
    if (ImGui::Checkbox("Triggered", &value)) forced = value;
    value = enabled;
    ImGui::Checkbox("Enabled", &value);
    enabled = value;
  }

  void interrupt(GpioEvent& ev) {
    if (ev.pin_id == endstop && ev.event == ev.GET_VALUE) {
      const bool contact = enabled && (forced || triggered());
      last_contact = contact;
      Gpio::set_pin_value(ev.pin_id, invert_logic ? !contact : contact);
    }
  }

private:
  const pin_type endstop;
  std::atomic_bool enabled = true;
  std::atomic_bool forced = false;
  std::atomic_bool last_contact = false;
  bool invert_logic;
  std::function<bool()> triggered;
};
