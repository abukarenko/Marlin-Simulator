// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once
#include <stdint.h>

// Native PWM adapter. The virtual hardware consumes a 16-bit duty cycle.
class NativeServo {
public:
  NativeServo();
  int8_t attach(int pin);
  int8_t attach(int pin, int minimum, int maximum);
  void detach();
  void write(int value);
  void writeMicroseconds(int value);
  void move(int value);
  int read();
  int readMicroseconds();
  bool attached();
private:
  static uint8_t next_index;
  uint8_t index;
  int pin = -1;
  int minimum = 544, maximum = 2400, pulse = 1500;
  bool active = false;
};
using hal_servo_t = NativeServo;
