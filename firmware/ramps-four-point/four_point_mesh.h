#pragma once
// Corners in clockwise order: front-left, front-right, back-right, back-left.
// u/v are fractions of the probing rectangle, independent of the stored mesh size.
inline float four_point_height(const float (&z)[4], const float u, const float v) {
  return (1 - v) * ((1 - u) * z[0] + u * z[1]) + v * ((1 - u) * z[3] + u * z[2]);
}
