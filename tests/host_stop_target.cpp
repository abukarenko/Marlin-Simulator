#include "../vendor/Marlin-2.1.2.9/Marlin/src/module/host_stop.h"
#include <cassert>
#include <cmath>
#include <cstdio>
int main() {
  assert(host_stop_target_z(5,250,true)==20);
  assert(host_stop_target_z(245,250,true)==250);
  assert(host_stop_target_z(250,250,true)==250);
  assert(host_stop_target_z(260,250,true)==260);
  assert(host_stop_target_z(5,250,false)==5);
  assert(host_stop_target_z(5,NAN,true)==5);
  puts("PASS STOP target: full retreat, limit, above limit, untrusted and invalid maximum");
}
