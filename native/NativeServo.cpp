// SPDX-License-Identifier: GPL-3.0-or-later
#include "../../inc/MarlinConfig.h"
#if HAS_SERVOS
#include "Servo.h"
#include "../../core/utility.h"

uint8_t NativeServo::next_index = 0;
NativeServo::NativeServo() : index(next_index++) {}
int8_t NativeServo::attach(const int value) { return attach(value, 544, 2400); }
int8_t NativeServo::attach(const int value, const int low, const int high) {
  if (index >= NUM_SERVOS) return -1;
  if (value > 0 || pin < 0) pin = value;
  if (pin < 0 || !isValidPin(pin) || low >= high) return -1;
  minimum = low; maximum = high;
  pinMode(pin, OUTPUT);
  active = true;
  return index;
}
void NativeServo::detach() {
  if (active) analogWrite(pin, 0);
  active = false;
}
void NativeServo::write(const int value) {
  writeMicroseconds(value < 544 ? minimum + constrain(value, 0, 180) * (maximum - minimum) / 180 : value);
}
void NativeServo::writeMicroseconds(const int value) {
  pulse = constrain(value, minimum, maximum);
  if (active) analogWrite(pin, uint32_t(pulse) * 65535UL / 20000UL);
}
void NativeServo::move(const int value) {
  constexpr uint16_t delays[] = SERVO_DELAY;
  static_assert(COUNT(delays) == NUM_SERVOS, "SERVO_DELAY must match NUM_SERVOS");
  if (attach(0) >= 0) {
    write(value);
    safe_delay(delays[index]);
    TERN_(DEACTIVATE_SERVOS_AFTER_MOVE, detach());
  }
}
int NativeServo::read() { return (pulse - minimum) * 180 / (maximum - minimum); }
int NativeServo::readMicroseconds() { return pulse; }
bool NativeServo::attached() { return active; }
#endif
