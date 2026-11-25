#pragma once
extern bool host_stop_pending;
void host_stop_request();
void host_stop_service();
inline float host_stop_target_z(const float current, const float maximum, const bool trusted) {
  if (!trusted || !(current < maximum)) return current;
  const float requested = current + 15.0f;
  return requested < maximum ? requested : maximum;
}
