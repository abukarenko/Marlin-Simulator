#include "visualisation.h"

#include <glm/vec3.hpp>
#include <glm/vec4.hpp>
#include <glm/mat4x4.hpp>
#include <glm/gtc/type_ptr.hpp>
#include <glm/gtc/matrix_transform.hpp>
#include <glm/gtc/epsilon.hpp>

#include <vector>
#include <array>
#include <imgui_internal.h>
#include <implot.h>

#include "resources/resources.h"

Visualisation::Visualisation(VirtualPrinter& virtual_printer) : virtual_printer(virtual_printer) {
  virtual_printer.on_kinematic_update = [this](kinematic_state& state){
    for (size_t i = 0; i < state.effector_position.size(); ++i) {
      this->set_head_position(i, state.effector_position[i]);
    }
  };

  for (int i = 0; i < EXTRUDERS; ++i) {
    extrusion.push_back({});
  }
}

Visualisation::~Visualisation() {
  destroy();
}

void Visualisation::create() {
  extrusion_program = renderer::ShaderProgram::create("data/shaders/extrusion.vs", "data/shaders/extrusion.fs", "data/shaders/extrusion.gs");
  default_program = renderer::ShaderProgram::create("data/shaders/default.vs","data/shaders/default.fs");

  framebuffer = new opengl_util::MsaaFrameBuffer();
  if (!((opengl_util::MsaaFrameBuffer*)framebuffer)->create(100, 100, 4)) {
    logger::warning("Failed to initialise MSAA Framebuffer falling back to TextureFramebuffer\n");
    delete framebuffer;
    framebuffer = new opengl_util::TextureFrameBuffer();
    if (!((opengl_util::TextureFrameBuffer*)framebuffer)->create(100,100)) {
      logger::error("Unable to initialise a Framebuffer\n");
    }
  }

  camera = { {37.0f, 121.0f, 129.0f}, {-192.0f, -25.0, 0.0f}, {0.0f, 1.0f, 0.0f}, float(100) / float(100), glm::radians(45.0f), 0.1f, 2000.0f};
  camera.generate();
  camera.look_at({build_plate_dimension.x * 0.5f, 0.0f, -build_plate_dimension.y * 0.5f});

  if (EXTRUDERS > 0) {
    auto mesh = renderer::create_mesh();
    auto buffer = renderer::Buffer<renderer::vertex_data_t>::create();
    buffer->data() = {
        renderer::vertex_data_t EFFECTOR_VERTEX(0.0, 0.0, 0.0, EFFECTOR_COLOR_1),
        EFFECTOR_VERTEX(-0.5, 0.5, 0.5, EFFECTOR_COLOR_2),
        EFFECTOR_VERTEX(-0.5, 0.5, -0.5, EFFECTOR_COLOR_3),
        EFFECTOR_VERTEX(0.0, 0.0, 0.0, EFFECTOR_COLOR_1),
        EFFECTOR_VERTEX(-0.5, 0.5, -0.5, EFFECTOR_COLOR_3),
        EFFECTOR_VERTEX(0.5, 0.5, -0.5, EFFECTOR_COLOR_2),
        EFFECTOR_VERTEX(0.0, 0.0, 0.0, EFFECTOR_COLOR_1),
        EFFECTOR_VERTEX(0.5, 0.5, -0.5, EFFECTOR_COLOR_2),
        EFFECTOR_VERTEX(0.5, 0.5, 0.5, EFFECTOR_COLOR_3),
        EFFECTOR_VERTEX(0.0, 0.0, 0.0, EFFECTOR_COLOR_1),
        EFFECTOR_VERTEX(0.5, 0.5, 0.5, EFFECTOR_COLOR_3),
        EFFECTOR_VERTEX(-0.5, 0.5, 0.5, EFFECTOR_COLOR_2),
        EFFECTOR_VERTEX(0.5, 0.5, -0.5, EFFECTOR_COLOR_2),
        EFFECTOR_VERTEX(-0.5, 0.5, -0.5, EFFECTOR_COLOR_3),
        EFFECTOR_VERTEX(-0.5, 0.5, 0.5, EFFECTOR_COLOR_2),
        EFFECTOR_VERTEX(0.5, 0.5, -0.5, EFFECTOR_COLOR_2),
        EFFECTOR_VERTEX(-0.5, 0.5, 0.5, EFFECTOR_COLOR_2),
        EFFECTOR_VERTEX(0.5, 0.5, 0.5, EFFECTOR_COLOR_3),
    };
    renderer::get_mesh_by_id(mesh)->buffer_vector<renderer::vertex_data_t>().push_back(buffer);

    m_extruder_mesh.push_back(mesh);
    for (int i = 1; i < EXTRUDERS; ++i) {
      m_extruder_mesh.push_back(mesh);
    }
  }

  for (auto& m : m_extruder_mesh) {
    auto mesh_object = renderer::get_mesh_by_id(m);
    mesh_object->set_shader_program(default_program);
    mesh_object->m_scale = effector_scale;
  }

  m_bed_mesh = renderer::create_mesh();
  auto mesh_object = renderer::get_mesh_by_id(m_bed_mesh);
  mesh_object->set_shader_program(renderer::ShaderProgram::create("data/shaders/default.vs", "data/shaders/bed.fs"));
  auto bed_mesh_buffer = renderer::Buffer<renderer::vertex_data_t>::create();
  mesh_object->buffer_vector<renderer::vertex_data_t>().push_back(bed_mesh_buffer);
  bed_mesh_buffer->data().reserve((BED_NUM_VERTEXES_PER_AXIS * BED_NUM_VERTEXES_PER_AXIS * 6));

  // Calculate the number of divisions (line segments) along each axis.
  const GLfloat x_div = GLfloat(build_plate_dimension.x) / (BED_NUM_VERTEXES_PER_AXIS - 1);
  const GLfloat y_div = GLfloat(-build_plate_dimension.y) / (BED_NUM_VERTEXES_PER_AXIS - 1);

  // Generate a subdivided plane mesh for the bed
  for (int row = 0; row < BED_NUM_VERTEXES_PER_AXIS - 1; ++row) {
    for (int col = 0; col < BED_NUM_VERTEXES_PER_AXIS - 1; ++col) {
      // For each division, calculate the coordinates of the four corners.
      GLfloat x1 = col * x_div;
      GLfloat x2 = (col + 1) * x_div;
      GLfloat y1 = row * y_div;
      GLfloat y2 = (row + 1) * y_div;

      // Create two triangles from the four corners.

      // |--/
      // | /
      // |/
      bed_mesh_buffer->add_vertex(BED_VERTEX(x2, y2));
      bed_mesh_buffer->add_vertex(BED_VERTEX(x1, y2));
      bed_mesh_buffer->add_vertex(BED_VERTEX(x1, y1));
      //    /|
      //   / |
      //  /--|
      bed_mesh_buffer->add_vertex(BED_VERTEX(x2, y2));
      bed_mesh_buffer->add_vertex(BED_VERTEX(x1, y1));
      bed_mesh_buffer->add_vertex(BED_VERTEX(x2, y1));
    }
  }

  auto kin = virtual_printer.get_component<KinematicSystem>("Cartesian Kinematic System");
  if(kin == nullptr) kin = virtual_printer.get_component<KinematicSystem>("Delta Kinematic System");
  if (kin != nullptr && kin->state.effector_position.size() == extrusion.size()) {
    size_t i = 0;
    for (auto state : kin->state.effector_position) {
      glm::vec4 pos = {state.position.x, state.position.z, state.position.y * -1.0, state.position.w};
      extrusion[i].last_position = pos;
      extrusion[i].position = pos;
      ++i;
    }
  }

  renderer::render_mesh(m_bed_mesh);
  for (auto mesh : m_extruder_mesh) {
    renderer::render_mesh(mesh);
  }
  renderer::render_list_is_ready();
  m_initialised = true;
}

void Visualisation::process_event(SDL_Event& e) { }
void Visualisation::gpio_event_handler(GpioEvent& event) { }
void Visualisation::on_position_update() { }

using millisec = std::chrono::duration<float, std::milli>;
void Visualisation::update() {
  std::scoped_lock extrusion_lock(extrusion_mutex);
  // auto now = clock.now();
  // float delta = std::chrono::duration_cast<std::chrono::duration<float>>(now - last_update).count();
  // last_update = now;
  auto effector_pos = extrusion[0].position;

  switch (follow_mode) {
    case FOLLOW_Z:  camera.position = glm::vec3(effector_pos.x, camera.position.y, effector_pos.z); break;
    case FOLLOW_XY: camera.position = glm::vec3(camera.position.x, effector_pos.y + follow_offset.y, camera.position.z); break;
    default: break;
  }
  camera.update_view();

  auto print_bed = virtual_printer.get_component<PrintBed>("Print Bed");

  auto bed_mesh = renderer::get_mesh_by_id(m_bed_mesh);
  if (print_bed->dirty) {
    GLfloat max_abs_z = 0.0f;
    // todo: move vertex generation
    auto bed_mesh_buffer = bed_mesh->buffer<renderer::vertex_data_t>();

    for (auto& v : bed_mesh_buffer->data()) {
      GLfloat z = print_bed->calculate_z({v.position.x, -v.position.z});
      v.position.y = z;
      max_abs_z = std::max(max_abs_z, abs(z));
    }

    for (auto&v : bed_mesh_buffer->data()) {
      GLfloat r = 0.0, g = 0.0, b = 0.0;

      if (print_bed->gradient_enabled) {
        GLfloat z = v.position.y;
        GLfloat *dest = z < 0.0 ? &r : &b;
        GLfloat gradient_range = std::max(1.0f, max_abs_z);

        *dest = std::min(1.0f, abs(z) / gradient_range);
        g = 0.5f - *dest;
      } else {
        // default color
        r = g = b = 0.5f;
      }

      v.color.r = r;
      v.color.g = g;
      v.color.b = b;
    }
    print_bed->dirty = false;
  }

  // update the position of the extruder mesh for visualisation
  size_t mesh_id = 0;
  bool draw_list_update = false;
  for (auto& ext : extrusion ) {
    auto pos = glm::vec3(ext.position.x, ext.position.y, ext.position.z);
    auto mesh_object = renderer::get_mesh_by_id(m_extruder_mesh[mesh_id]);
    if (mesh_object->m_position != pos) {
      mesh_object->m_position = pos;
      mesh_object->m_transform_dirty = true;
    }
    mesh_object->m_visible = (follow_mode != FOLLOW_Z);

    if (ext.should_clear) {
      draw_list_update = true;
      ext.should_clear = false;
      ext.last_position = ext.position;
      ext.last_extrusion_check = ext.position;
      ext.extruding = ext.last_extruding = false;
      auto obj = renderer::get_mesh_by_id(ext.mesh);
      auto old_mesh_index = ext.mesh;
      ext.mesh = renderer::create_mesh();
      auto new_mesh_object = renderer::get_mesh_by_id(ext.mesh);

      if (obj != nullptr) {
        new_mesh_object->set_shader_program(obj->m_shader_instance);
        renderer::destroy_mesh(old_mesh_index);
      } else {
        new_mesh_object->set_shader_program(extrusion_program);
        new_mesh_object->m_shader_instance->set_uniform("u_layer_thickness", &extrude_thickness);
        new_mesh_object->m_shader_instance->set_uniform("u_layer_width", &extrude_width);
        new_mesh_object->m_shader_instance->set_uniform("u_view_position", &camera.position);
      }

    }
    auto extrusion_mesh_object = renderer::get_mesh_by_id(ext.mesh);
    if (extrusion_mesh_object != nullptr) {
      extrusion_mesh_object->m_visible = ext.is_visible;
    }

    mesh_id ++;
  }

  if (draw_list_update) {
    renderer::render_mesh(m_bed_mesh);
    for (auto mesh : m_extruder_mesh) {
      renderer::render_mesh(mesh);
    }
    for (auto& ext : extrusion) {
      renderer::render_mesh(ext.mesh);
    }
    renderer::render_list_is_ready();
  }

  renderer::render(camera.proj * camera.view);
}

void Visualisation::destroy() {
  if(framebuffer != nullptr) {
    framebuffer->release();
    delete framebuffer;
  }
  extrusion_program.reset();
  default_program.reset();
  renderer::destroy();
}

void Visualisation::set_head_position(size_t hotend_index, extruder_state& state) {
  if (!m_initialised || hotend_index >= extrusion.size()) return;
  glm::vec4 sim_pos = state.position;
  glm::vec4 position = {sim_pos.x, sim_pos.z, sim_pos.y * -1.0, sim_pos.w}; // correct for opengl coordinate system
  std::scoped_lock extrusion_lock(extrusion_mutex);
  auto& extruder = extrusion[hotend_index];
  glm::vec3 extrude_color = state.color;

  auto active_mesh = renderer::get_mesh_by_id(extruder.mesh);
  if (!active_mesh) return;
  auto active_buffer = active_mesh->buffer<renderer::vertex_data_t>();

  if (position != extruder.position) {

    // smooths out extrusion over a minimum length to fill in gaps todo: implement an simulation to do this better
    // also use Z (Y in opengl) change to reduce minimum extrude length
    if (glm::length(glm::vec3(position) - glm::vec3(extruder.last_extrusion_check)) > m_config.extrusion_check_min_line_length || glm::abs(position.y - extruder.last_extrusion_check.y) > m_config.extrusion_check_max_vertical_deviation) {
      extruder.extruding = position.w - extruder.last_extrusion_check.w > 0.0f;
      extruder.last_extrusion_check = position;
    }

    if (active_buffer != nullptr && active_buffer->size() > 1 && active_buffer->size() < renderer::MAX_BUFFER_SIZE) {

      if (glm::length(glm::vec3(position) - glm::vec3(extruder.last_position)) > m_config.extrusion_segment_minimum_length) { // smooth out the path so the model renders with less geometry, rendering each individual step hurts the fps
        if((points_are_collinear(position, active_buffer->cdata().end()[-3].position, active_buffer->cdata().end()[-2].position, m_config.extrusion_segment_collinearity_max_deviation) && extruder.extruding == extruder.last_extruding) || ( extruder.extruding == false && extruder.last_extruding == false)) {
          // collinear and extrusion state has not changed so we can just change the current point.
          active_buffer->data().end()[-2].position = position;
          active_buffer->data().end()[-1].position = position;
        } else { // new point is not collinear with current path add new point
          active_buffer->data().end()[-1] = {position, {0.0, 1.0, 0.0}, {extrude_color, extruder.extruding}};
          active_buffer->add_vertex(active_buffer->cdata().back());
        }
        extruder.last_position = position;
        extruder.last_extruding = extruder.extruding;
      }

    } else { // need to change geometry buffer
      if (active_buffer == nullptr) {
        auto buffer = renderer::Buffer<renderer::vertex_data_t>::create();
        buffer->data().reserve(renderer::MAX_BUFFER_SIZE);
        buffer->m_geometry_type = renderer::GeometryPrimitive::LINE_STRIP_ADJACENCY;
        buffer->data().push_back({position, {0.0, 1.0, 0.0}, {extrude_color, 0.0}});
        active_mesh->buffer_vector<renderer::vertex_data_t>().push_back(buffer);
        active_buffer = buffer;
        extruder.last_extrusion_check = position;
      } else {
        renderer::vertex_data_t last_vertex = active_buffer->cdata().back();
        auto buffer = renderer::Buffer<renderer::vertex_data_t>::create();
        buffer->data().reserve(renderer::MAX_BUFFER_SIZE);
        buffer->m_geometry_type = renderer::GeometryPrimitive::LINE_STRIP_ADJACENCY;
        buffer->add_vertex(last_vertex);
        buffer->add_vertex({position, {0.0, 1.0, 0.0}, {extrude_color, extruder.extruding}});
        buffer->add_vertex(buffer->cdata().back());

        active_buffer = buffer;
        active_mesh->buffer_vector<renderer::vertex_data_t>().push_back(buffer);
      }
      // extra dummy verticies for line strip adjacency
      active_buffer->add_vertex(active_buffer->cdata().back());
      active_buffer->add_vertex(active_buffer->cdata().back());
      extruder.last_position = position;
    }
    extruder.position = position;
  }
}

bool Visualisation::points_are_collinear(const glm::vec3 a, const glm::vec3 b, const glm::vec3 c, double const threshold) const {
  return glm::abs(glm::dot(b - a, c - a) - (glm::length(b - a) * glm::length(c - a))) < threshold;
}

void Visualisation::ui_viewport_menu_callback(UiWindow*) {
  std::scoped_lock extrusion_lock(extrusion_mutex);
  bool open_settings = false;
  if (ImGui::BeginMenuBar()) {
    if (ImGui::BeginMenu("Camera")) {
      if (ImGui::MenuItem("Reset")) {
        camera.position = {37.0f, 121.0f, 129.0f};
        camera.rotation = {-192.0f, -25.0, 0.0f};
        camera.up       = {0.0f, 1.0f, 0.0f};
        camera.look_at({build_plate_dimension.x * 0.5f, 0.0f, -build_plate_dimension.y * 0.5f});
      }
      // if (ImGui::BeginMenu("Mode")) {
      //   if (ImGui::MenuItem("Fly", nullptr, true, true)) { }
      //   if (ImGui::MenuItem("Orbit", nullptr, false, true)) { }
      //   ImGui::EndMenu();
      // }
      if (ImGui::BeginMenu("Focus View")) {
        if (ImGui::MenuItem("Centre X (Right)")) {
          camera.position = {build_plate_dimension.x, 10.0f, -(build_plate_dimension.y / 2.0f)};
          camera.rotation = {-90.0f, 0.0, 0.0f};
        }
        if (ImGui::MenuItem("Centre Y (Front)")) {
          camera.position = {build_plate_dimension.x / 2.0f, 10.0f, 0.0f};
          camera.rotation = {-180.0f, 0.0, 0.0f};
        }
        if (ImGui::MenuItem("Centre Z (Top)")) {
          camera.position = {build_plate_dimension.x / 2.0, 200.0f, -(build_plate_dimension.y / 2.0f)};
          camera.rotation = {90.0f, -90.0, 0.0f};
        }
        ImGui::EndMenu();
      }
      ImGui::EndMenu();
    }
    if (ImGui::BeginMenu("Extrusion")) {
      if (ImGui::MenuItem("Settings")) {
        open_settings = true;
      }
      if (ImGui::MenuItem("Hide All")) {
        for (auto& ext : extrusion) {
          ext.is_visible = false;
        }
      }
      if (ImGui::MenuItem("Show All")) {
        for (auto& ext : extrusion) {
          ext.is_visible = true;
        }
      }
      if (ImGui::MenuItem("Clear Bed")) {
        for (auto& ext : extrusion) {
          ext.should_clear = true;
        }
      }
      if (ImGui::BeginMenu("Visible")) {
        size_t count = 0;
        for (auto& extruder : extrusion) {
          bool vis          = extruder.is_visible;
          std::string label = "Extrusion ";
          label += std::to_string(count);
          if (ImGui::Checkbox(label.c_str(), &vis)) {
            extruder.is_visible = vis;
          }
          ++count;
        }
        ImGui::EndMenu();
      }
      ImGui::EndMenu();
    }
    if (ImGui::MenuItem("Clear Image")) {
      for (auto& ext : extrusion) ext.should_clear = true;
    }
    ImGui::EndMenuBar();
  }

  if (open_settings) {
    ImGui::OpenPopup("Extrusion Settings");
  }
}

void Visualisation::ui_viewport_callback(UiWindow* window) {
  std::scoped_lock extrusion_lock(extrusion_mutex);
  auto now = clock.now();
  float delta = std::chrono::duration_cast<std::chrono::duration<float>>(now- last_update).count();
  last_update = now;

  Viewport& viewport = *((Viewport*)window);
  auto& ex = extrusion[0];

  if (viewport.dirty) {
    viewport.viewport_size.x = viewport.viewport_size.x > 0 ? viewport.viewport_size.x : 1;
    viewport.viewport_size.y = viewport.viewport_size.y > 0 ? viewport.viewport_size.y : 1;
    framebuffer->update(viewport.viewport_size.x, viewport.viewport_size.y);
    viewport.texture_id = framebuffer->texture_id();
    camera.update_aspect_ratio(viewport.viewport_size.x / viewport.viewport_size.y);
  }

  if (viewport.focused) {
    if (ImGui::IsKeyDown(ImGuiKey_W)) {
      camera.position += camera.speed * camera.direction * delta;
    }
    if (ImGui::IsKeyDown(ImGuiKey_S)) {
      camera.position -= camera.speed * camera.direction * delta;
    }
    if (ImGui::IsKeyDown(ImGuiKey_A)) {
      camera.position -= glm::normalize(glm::cross(camera.direction, camera.up)) * camera.speed * delta;
    }
    if (ImGui::IsKeyDown(ImGuiKey_D)) {
      camera.position += glm::normalize(glm::cross(camera.direction, camera.up)) * camera.speed * delta;
    }
    if (ImGui::IsKeyDown(ImGuiKey_Space)) {
      camera.position += camera.world_up * camera.speed * delta;
    }
    if (ImGui::IsKeyDown(ImGuiKey_LeftShift)) {
      camera.position -= camera.world_up * camera.speed * delta;
    }
    if (ImGui::IsKeyPressed(ImGuiKey_F)) {
      follow_mode = follow_mode == FOLLOW_Z ? FOLLOW_NONE : FOLLOW_Z;
      if (follow_mode != FOLLOW_NONE) {
        camera.position = glm::vec3(ex.position.x, ex.position.y + 10.0, ex.position.z);
        camera.rotation.y = -89.99999;
      }
    }
    if (ImGui::IsKeyPressed(ImGuiKey_G)) {
      follow_mode = follow_mode == FOLLOW_XY ? FOLLOW_NONE : FOLLOW_XY;
      if (follow_mode != FOLLOW_NONE)
        follow_offset = camera.position - glm::vec3(ex.position);
    }
    if (ImGui::GetIO().MouseWheel != 0 && viewport.hovered) {
      camera.position += 10.0f * camera.speed * camera.direction * (viewport.standalone ? 1.f/60.f : delta) * ImGui::GetIO().MouseWheel;
    }
  }

  bool last_mouse_captured = mouse_captured;
  if (!mouse_captured && viewport.hovered && (ImGui::IsMouseClicked(0) || ImGui::IsMouseClicked(1))) {
    captured_mouse_button = ImGui::IsMouseClicked(1) ? 1 : 0;
    mouse_captured = true;
  } else if (mouse_captured && (!ImGui::IsMouseDown(captured_mouse_button) || !viewport.focused)) {
    mouse_captured = false;
  }

  // IMGUI: Relative Mouse mode within a window does not seem to be supported through the imgui mouse api
  if (mouse_captured && !last_mouse_captured) {
    ImVec2 mouse_pos = ImGui::GetMousePos();
    mouse_lock_pos = {mouse_pos.x, mouse_pos.y};
    if (!viewport.standalone) {
      SDL_SetWindowGrab(SDL_GL_GetCurrentWindow(), SDL_TRUE);
      SDL_SetRelativeMouseMode(SDL_TRUE);
      SDL_GetRelativeMouseState(nullptr, nullptr);
    }
  } else if (!mouse_captured && last_mouse_captured) {
    if (!viewport.standalone) {
      SDL_SetRelativeMouseMode(SDL_FALSE);
      SDL_SetWindowGrab(SDL_GL_GetCurrentWindow(), SDL_FALSE);
      SDL_WarpMouseInWindow(SDL_GL_GetCurrentWindow(), mouse_lock_pos.x, mouse_lock_pos.y);
      SDL_GetRelativeMouseState(nullptr, nullptr);
    }
  } else if (mouse_captured) {
    int rel_x, rel_y;
    if(viewport.standalone) { rel_x=int(ImGui::GetIO().MouseDelta.x); rel_y=int(ImGui::GetIO().MouseDelta.y); }
    else SDL_GetRelativeMouseState(&rel_x, &rel_y);
    const glm::vec3 center{build_plate_dimension.x * 0.5f, 0.0f, -build_plate_dimension.y * 0.5f};
    if (captured_mouse_button == 1) {
      camera.update_view();
      const float distance = std::max(glm::length(camera.position - center), 1.0f);
      const float scale = 2.0f * distance * std::tan(camera.fov * 0.5f)
                          / std::max(viewport.viewport_size.y, 1.0f);
      camera.position += (-glm::normalize(glm::cross(camera.direction, camera.up)) * float(rel_x)
                           + glm::normalize(camera.up) * float(rel_y)) * scale;
    } else {
      const auto offset = camera.position - center;
      const float radius = std::max(glm::length(offset), 1.0f);
      const float azimuth = std::atan2(offset.x, offset.z) - glm::radians(rel_x * 0.2f);
      const float elevation = glm::clamp(std::asin(glm::clamp(offset.y / radius, -1.0f, 1.0f))
                                        + glm::radians(rel_y * 0.2f), glm::radians(-89.0f), glm::radians(89.0f));
      camera.position = center + radius * glm::vec3(std::cos(elevation) * std::sin(azimuth),
                                                  std::sin(elevation), std::cos(elevation) * std::cos(azimuth));
      camera.look_at(center);
    }
    follow_mode = FOLLOW_NONE;
  }

  // Draw in image coordinates, clipped so markers never cover neighbouring panels.
  auto* draw = ImGui::GetWindowDrawList();
  draw->PushClipRect(viewport.image_min, viewport.image_max, true);
  const ImU32 marker = IM_COL32(255, 190, 70, 255);
  auto project = [&](const glm::vec3& point, ImVec2& screen) {
    const auto clip = camera.proj * camera.view * glm::vec4(point, 1.0f);
    if (clip.w <= 0.001f || clip.z < -clip.w || clip.z > clip.w) return false;
    const auto ndc = glm::vec3(clip) / clip.w;
    screen = {viewport.image_min.x + (ndc.x + 1.0f) * 0.5f * viewport.viewport_size.x,
              viewport.image_min.y + (1.0f - ndc.y) * 0.5f * viewport.viewport_size.y};
    return true;
  };
  auto bed = virtual_printer.get_component<PrintBed>("Print Bed");
  glm::vec3 foot{ex.position.x, bed ? bed->calculate_z({ex.position.x, -ex.position.z}) : 0.0f, ex.position.z};
  ImVec2 tip_screen, foot_screen;
  if (project(glm::vec3(ex.position), tip_screen) && project(foot, foot_screen)) {
    const float dx = foot_screen.x - tip_screen.x, dy = foot_screen.y - tip_screen.y;
    const float length = std::sqrt(dx * dx + dy * dy);
    for (float t = 0; t < length; t += 10.0f) {
      const float end = std::min(t + 5.0f, length);
      draw->AddLine({tip_screen.x + dx * t / length, tip_screen.y + dy * t / length},
                    {tip_screen.x + dx * end / length, tip_screen.y + dy * end / length}, marker, 1.5f);
    }
    draw->AddCircleFilled(foot_screen, 3.5f, marker);
  }
  char coordinates[128];
  std::snprintf(coordinates, sizeof(coordinates), "X %.3f\nY %.3f\nZ %.3f", ex.position.x, -ex.position.z, ex.position.y);
  const auto text_size = ImGui::CalcTextSize(coordinates);
  const ImVec2 text_pos{viewport.image_max.x - text_size.x - 12.0f, viewport.image_min.y + 12.0f};
  draw->AddRectFilled({text_pos.x - 6, text_pos.y - 4},
                      {text_pos.x + text_size.x + 6, text_pos.y + text_size.y + 4}, IM_COL32(20, 25, 30, 155), 4);
  draw->AddText(text_pos, marker, coordinates);
  draw->PopClipRect();

  if (ImGui::BeginPopup("Extrusion Settings")) {
    ImGui::PushItemWidth(150);
    ImGui::Text("Extrude Width    ");
    ImGui::PopItemWidth();
    ImGui::PushItemWidth(50);
    ImGui::SameLine();
    ImGui::InputFloat("##Extrude_Width", &extrude_width);
    ImGui::PopItemWidth();

    ImGui::PushItemWidth(150);
    ImGui::Text("Extrude Thickness");
    ImGui::PopItemWidth();
    ImGui::PushItemWidth(50);
    ImGui::SameLine();
    ImGui::InputFloat("##Extrude_Thickness", &extrude_thickness);
    ImGui::PopItemWidth();

    ImGui::PushItemWidth(150);
    ImGui::Text("Extrusion Check Min");
    ImGui::PopItemWidth();
    ImGui::PushItemWidth(100);
    ImGui::InputDouble("##Extrusion_Check_Min", &m_config.extrusion_check_min_line_length);
    ImGui::PopItemWidth();

    ImGui::PushItemWidth(150);
    ImGui::Text("Extrusion Check Vertical Max");
    ImGui::PopItemWidth();
    ImGui::PushItemWidth(100);
    ImGui::InputDouble("##Extrusion_Check_Vertical_Max", &m_config.extrusion_check_max_vertical_deviation);
    ImGui::PopItemWidth();

    ImGui::PushItemWidth(150);
    ImGui::Text("Extrusion Segment Min Length");
    ImGui::PopItemWidth();
    ImGui::PushItemWidth(100);
    ImGui::InputDouble("##Extrusion_Segment_Min_Length", &m_config.extrusion_segment_minimum_length);
    ImGui::PopItemWidth();

    ImGui::PushItemWidth(150);
    ImGui::Text("Extrusion Collinearity Max Deviation");
    ImGui::PopItemWidth();
    ImGui::PushItemWidth(100);
    ImGui::InputDouble("##Extrusion_Collinearity_Max_Deviation", &m_config.extrusion_segment_collinearity_max_deviation);
    ImGui::PopItemWidth();
    ImGui::EndPopup();
  }
};

void Visualisation::ui_info_callback(UiWindow* w) {
  ImGui::Text("Application average %.3f ms/frame", 1000.0f / ImGui::GetIO().Framerate);
  ImGui::Text("%.1f FPS", ImGui::GetIO().Framerate);

  if (ImGui::Button("Reload Shaders")) {
    if (!extrusion_program->reload()) {
      logger::warning("Shader Reload Failed!\n");
    }
  }
}

