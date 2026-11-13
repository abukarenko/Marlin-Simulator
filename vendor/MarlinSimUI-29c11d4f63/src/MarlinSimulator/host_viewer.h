#pragma once
#include <SDL2/SDL_syswm.h>
#include <sstream>
#include <filesystem>

// Display-only entry point. No Marlin setup/loop, socket listener or command execution.
inline int run_host_viewer(uintptr_t parent_value) {
  SetEnvironmentVariableA("MARLIN_VIEWER_EMBEDDED", "1");
  SDL_Init(0);
  Application app(true);
  auto* window = static_cast<SDL_Window*>(app.window.getHandle());
  SDL_SysWMinfo info{}; SDL_VERSION(&info.version);
  if (!SDL_GetWindowWMInfo(window, &info)) return 3;
  auto hwnd = info.info.win.window;
  auto parent = reinterpret_cast<HWND>(parent_value);
  SetWindowLongPtr(hwnd, GWL_STYLE, WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN);
  SetParent(hwnd, parent);
  SetWindowPos(hwnd, nullptr, 0, 0, 1, 1, SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
  SDL_ShowWindow(window);
  SDL_GL_SetSwapInterval(0); // The embedded viewer uses its own 30 Hz frame budget.
  ImGui::GetIO().ConfigFlags |= ImGuiConfigFlags_NoMouseCursorChange;
  ImGui::GetIO().MouseDrawCursor=false;
  SDL_SetRelativeMouseMode(SDL_FALSE);
  SDL_ShowCursor(SDL_ENABLE);
  std::unique_ptr<SDL_Cursor,decltype(&SDL_FreeCursor)> arrow(SDL_CreateSystemCursor(SDL_SYSTEM_CURSOR_ARROW),SDL_FreeCursor);
  if(arrow) SDL_SetCursor(arrow.get());
  // Initialize the extrusion meshes before importing points.
  app.sim.vis.update();
  std::string buffer;
  auto input = GetStdHandle(STD_INPUT_HANDLE);
  auto& vis = app.sim.vis;
  struct SceneSegment { float p[6]; uint8_t extruding; int32_t command; };
  std::vector<SceneSegment> scene;
  size_t printed_cursor=0;
  float display_progress=0, solid_e=0;
  glm::vec3 filament_color{1.f,.5f,0.f};
  bool ghost_visible=true, extrusion_visible=true;
  auto ghost=renderer::Mesh::create();
  auto ghost_program=std::make_shared<renderer::ShaderProgram>(renderer::ShaderProgram::load_program(
    R"(#version 330 core
      layout(location=0) in vec3 position;
      layout(location=1) in vec3 info;
      uniform mat4 u_mvp;
      flat out float command_index;
      void main(){ command_index=info.x; gl_Position=u_mvp*vec4(position,1.0); })",
    R"(#version 330 core
      flat in float command_index;
      uniform float u_progress;
      out vec4 fragment;
      void main(){ if(command_index<u_progress) discard; fragment=vec4(.30,.32,.34,.18); })"));
  ghost_program->build_cache();
  ghost->set_shader_program(ghost_program);
  ghost->m_shader_instance->set_uniform("u_progress",&display_progress);
  app.overlay_render=[&]() {
    if(!ghost_visible || !extrusion_visible) return;
    const bool blended=glIsEnabled(GL_BLEND); GLboolean depth_write; glGetBooleanv(GL_DEPTH_WRITEMASK,&depth_write);
    GLint src,dst,srca,dsta; glGetIntegerv(GL_BLEND_SRC_RGB,&src); glGetIntegerv(GL_BLEND_DST_RGB,&dst); glGetIntegerv(GL_BLEND_SRC_ALPHA,&srca); glGetIntegerv(GL_BLEND_DST_ALPHA,&dsta);
    glEnable(GL_BLEND); glBlendFunc(GL_SRC_ALPHA,GL_ONE_MINUS_SRC_ALPHA); glDepthMask(GL_FALSE);
    ghost->render(vis.camera.proj*vis.camera.view);
    glDepthMask(depth_write); glBlendFuncSeparate(src,dst,srca,dsta); if(!blended) glDisable(GL_BLEND);
  };
  auto clear_solid=[&]() {
    for(auto& ex:vis.extrusion) ex.should_clear=true;
    vis.update(); solid_e=0;
  };
  auto append_solid=[&](const SceneSegment& segment) {
    const auto& p=segment.p;
    extruder_state state{}; state.color=filament_color;
    state.position={p[0],p[1],p[2],solid_e}; vis.set_head_position(0,state);
    if(segment.extruding&1) solid_e+=glm::length(glm::vec3(p[3]-p[0],p[4]-p[1],p[5]-p[2]))*.1f;
    state.position={p[3],p[4],p[5],solid_e}; vis.set_head_position(0,state);
  };
  glm::vec3 head_from{}, head_target{};
  bool head_received=false;
  auto head_time=std::chrono::steady_clock::now();
  double head_duration=.1, measured_fps=0;
  unsigned frame_count=0, head_count=0, smooth_frames=0;
  auto fps_time=head_time;
  bool open_settings=false;
  UserInterface::getElement<Viewport>("Viewport")->menu_callback = [&](UiWindow* w) {
    vis.ui_viewport_menu_callback(w);
    if(open_settings) { ImGui::OpenPopup("Extrusion Settings"); open_settings=false; }
  };
  auto command = [&](const std::string& line) {
    std::istringstream stream(line); std::string op; stream >> op;
    if (op == "QUIT") app.active = false;
    else if (op == "SETTINGS") open_settings=true;
    else if (op == "VISIBLE") { int value=1; stream >> value; extrusion_visible=value!=0; for(auto& ex:vis.extrusion) ex.is_visible=extrusion_visible; }
    else if (op == "PROGRESS") {
      int progress=0; if(stream>>progress) {
        if(progress<display_progress) { clear_solid(); printed_cursor=0; }
        display_progress=std::max(0,progress);
      }
    }
    else if (op == "SCENE") {
      std::string path; std::getline(stream >> std::ws, path);
      std::ifstream file(std::filesystem::u8path(path), std::ios::binary);
      uint32_t count = 0; file.read(reinterpret_cast<char*>(&count), sizeof(count));
      if (!file || count > 10000000) return;
      file.read(reinterpret_cast<char*>(&filament_color[0]),sizeof(float)*3);
      clear_solid(); printed_cursor=0; display_progress=0; scene.clear(); scene.reserve(count); ghost->m_buffer.clear(); ghost_visible=true;
      std::shared_ptr<renderer::Buffer<renderer::vertex_data_t>> wire;
      for (uint32_t i=0; i<count; ++i) {
        SceneSegment segment{};
        file.read(reinterpret_cast<char*>(segment.p),sizeof(segment.p)); file.read(reinterpret_cast<char*>(&segment.extruding),1); file.read(reinterpret_cast<char*>(&segment.command),4);
        if (!file) break;
        scene.push_back(segment);
        if((segment.extruding&3)!=3) continue; // Ghost only the exterior contour; dense infill would obscure transparency.
        if(!wire || wire->size()>=renderer::MAX_BUFFER_SIZE-2) {
          wire=renderer::Buffer<renderer::vertex_data_t>::create(); wire->m_geometry_type=renderer::GeometryPrimitive::LINES;
          ghost->m_buffer.push_back(wire);
        }
        const auto& p=segment.p;
        wire->add_vertex({{p[0],p[2],-p[1]},{float(segment.command),0,0},{1,1,1,1}});
        wire->add_vertex({{p[3],p[5],-p[4]},{float(segment.command),0,0},{1,1,1,1}});
      }
      file.close();
      std::error_code error; std::filesystem::remove(std::filesystem::u8path(path), error);
      printf("VIEWER_SCENE %u\n", count);
    }
    else if (op == "HEAD") {
      float x,y,z; if(stream >> x >> y >> z) {
        const auto now=std::chrono::steady_clock::now();
        head_from=glm::vec3(vis.extrusion[0].position);
        head_target={x,z,-y};
        head_duration=std::clamp(std::chrono::duration<double>(now-head_time).count(),.04,.15);
        if(!head_received) { head_from=head_target; vis.extrusion[0].position=glm::vec4(head_target,0); }
        head_time=now; head_received=true; ++head_count;
      }
    }
    else if (op == "STATS") printf("VIEWER_STATS fps=%.1f heads=%u interpolated=%u printed=%zu segments=%zu cursor=%d relative=%d\n",measured_fps,head_count,smooth_frames,printed_cursor,scene.size(),SDL_ShowCursor(SDL_QUERY),SDL_GetRelativeMouseMode());
    else if (op == "RESET") {
      vis.camera.position = {37,121,129}; vis.camera.look_at({build_plate_dimension.x*.5f,0,-build_plate_dimension.y*.5f});
    }
    else if (op == "TOP") { vis.camera.position={110,300,-110}; vis.camera.rotation={180,-89.999f,0}; }
    else if (op == "FRONT") { vis.camera.position={110,10,100}; vis.camera.rotation={180,0,0}; }
    else if (op == "RIGHT") { vis.camera.position={320,10,-110}; vis.camera.rotation={-90,0,0}; }
    else if (op == "CLEAR") { ghost_visible=false; clear_solid(); }
    else if (op == "FOLLOW") { int mode=0; stream >> mode; vis.follow_mode=mode ? FOLLOW_XY : FOLLOW_NONE; vis.follow_offset=vis.camera.position-glm::vec3(vis.extrusion[0].position); }
    else if (op == "SHOT") {
      std::string path; std::getline(stream >> std::ws,path);
      int w,h; SDL_GL_GetDrawableSize(window,&w,&h);
      std::vector<uint8_t> pixels(w*h*4), flipped(pixels.size());
      glReadBuffer(GL_FRONT); glReadPixels(0,0,w,h,GL_RGBA,GL_UNSIGNED_BYTE,pixels.data());
      for(int y=0;y<h;++y) std::copy_n(pixels.data()+(h-1-y)*w*4,w*4,flipped.data()+y*w*4);
      auto surface=SDL_CreateRGBSurfaceWithFormatFrom(flipped.data(),w,h,32,w*4,SDL_PIXELFORMAT_RGBA32);
      if(surface) { SDL_SaveBMP(surface,path.c_str()); SDL_FreeSurface(surface); }
    }
  };
  while (app.active && IsWindow(parent)) {
    const auto frame_start=std::chrono::steady_clock::now();
    RECT rect; GetClientRect(parent,&rect);
    int w,h; SDL_GetWindowSize(window,&w,&h);
    if(w != rect.right || h != rect.bottom) { SDL_SetWindowSize(window,std::max(1L,rect.right),std::max(1L,rect.bottom)); SetWindowPos(hwnd,nullptr,0,0,rect.right,rect.bottom,SWP_NOZORDER); }
    DWORD available=0;
    if (!PeekNamedPipe(input,nullptr,0,nullptr,&available,nullptr)) break;
    while(available) {
      char data[4096]; DWORD read=0;
      if(!ReadFile(input,data,std::min<DWORD>(sizeof(data),available),&read,nullptr) || !read) break;
      buffer.append(data,read); size_t newline;
      while((newline=buffer.find('\n'))!=std::string::npos) { auto line=buffer.substr(0,newline); if(!line.empty() && line.back()=='\r') line.pop_back(); command(line); buffer.erase(0,newline+1); }
      if(!PeekNamedPipe(input,nullptr,0,nullptr,&available,nullptr)) break;
    }
    while(printed_cursor<scene.size() && scene[printed_cursor].command<display_progress) append_solid(scene[printed_cursor++]);
    if(head_received) {
      const float t=std::clamp(float(std::chrono::duration<double>(frame_start-head_time).count()/head_duration),0.f,1.f);
      vis.extrusion[0].position=glm::vec4(glm::mix(head_from,head_target,t),0);
      if(t>0 && t<1 && head_from!=head_target) ++smooth_frames;
    }
    app.update(); app.render();
    ++frame_count;
    const auto now=std::chrono::steady_clock::now();
    const double sample=std::chrono::duration<double>(now-fps_time).count();
    if(sample>=1.) { measured_fps=frame_count/sample; frame_count=0; fps_time=now; }
    const double remaining=1000./30.-std::chrono::duration<double,std::milli>(now-frame_start).count();
    if(remaining>0) SDL_Delay(static_cast<Uint32>(std::ceil(remaining)));
  }
  return 0;
}
