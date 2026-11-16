#include "../firmware/ramps-four-point/four_point_mesh.h"
#include <cassert>
#include <cmath>
#include <cstdio>

int main() {
  const float flat[] = { .5f,.5f,.5f,.5f },
              plane[] = { .1f,.3f,.7f,.5f },
              saddle[] = { 0,1,0,1 };
  assert(four_point_height(plane,0,0)==plane[0]);
  assert(four_point_height(plane,1,0)==plane[1]);
  assert(four_point_height(plane,1,1)==plane[2]);
  assert(four_point_height(plane,0,1)==plane[3]);
  for(int x=0;x<10;x++) for(int y=0;y<9;y++) {
    const float u=x/9.f,v=y/8.f;
    assert(std::abs(four_point_height(flat,u,v)-.5f)<1e-6f);
    assert(std::abs(four_point_height(plane,u,v)-(.1f+.2f*u+.4f*v))<1e-6f);
    assert(std::abs(four_point_height(saddle,u,v)-(u+v-2*u*v))<1e-6f);
  }
  puts("PASS firmware interpolation: four corners, flat bed, tilt, saddle, complete 10x9 mesh.");
}
