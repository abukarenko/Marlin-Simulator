#include "../inc/MarlinConfig.h"
#include "host_stop.h"
#include "../MarlinCore.h"
#include "motion.h"
#include "planner.h"
#include "temperature.h"
#include "../gcode/queue.h"
#if HAS_MEDIA
  #include "../sd/cardreader.h"
#endif
bool host_stop_pending = false;
void host_stop_request() {
  if (host_stop_pending) return;
  host_stop_pending = true;
  wait_for_heatup = false;
  TERN_(HAS_RESUME_CONTINUE, wait_for_user = false);
  thermalManager.disable_all_heaters();
  thermalManager.zero_fan_speeds();
  queue.clear();
  quickstop_stepper();
}
void host_stop_service() {
  if (!host_stop_pending) return;
  queue.clear();
  #if HAS_MEDIA
    if (card.isPrinting()) card.abortFilePrintNow(TERN_(SD_RESORT, true));
  #endif
  planner.synchronize();
  // The interrupted G-code handler may have assigned its old destination after
  // quickstop returned. Re-read actual counters only after that handler unwinds.
  set_current_from_steppers_for_axis(ALL_AXES_ENUM);
  sync_plan_position();
  const bool known = axis_is_trusted(Z_AXIS);
  const float before = current_position.z;
  float target = before;
  #if HAS_SOFTWARE_ENDSTOPS && ENABLED(MAX_SOFTWARE_ENDSTOP_Z)
    if (known && isfinite(before) && isfinite(soft_endstop.max.z))
      target = host_stop_target_z(before, soft_endstop.max.z, known);
  #endif
  host_stop_pending = false;
  if (target > before) {
    const bool enabled = soft_endstop._enabled, loose = soft_endstop._loose;
    soft_endstop._enabled = true; soft_endstop._loose = false;
    do_blocking_move_to_z(target, 3.0f);
    soft_endstop._enabled = enabled; soft_endstop._loose = loose;
  }
  queue.clear();
  SERIAL_ECHOPGM("HOST_STOP_DONE V1 LIFT "); SERIAL_ECHO_F(current_position.z - before, 3);
  SERIAL_ECHOPGM(" Z "); SERIAL_ECHO_F(current_position.z, 3);
  SERIAL_ECHOPGM(" X "); SERIAL_ECHO_F(current_position.x, 3);
  SERIAL_ECHOPGM(" Y "); SERIAL_ECHO_F(current_position.y, 3);
  SERIAL_ECHOPGM(" HOMED "); SERIAL_ECHOLN(known ? 1 : 0);
}
