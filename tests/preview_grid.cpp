#define SDL_MAIN_HANDLED
#include <SDL2/SDL.h>
#include <glad/glad.h>
#include "resource_data.h"
#include <iostream>
#include <stdexcept>

GLuint shader(GLenum kind, const char* text) {
  GLuint value = glCreateShader(kind);
  glShaderSource(value, 1, &text, nullptr); glCompileShader(value);
  GLint valid; glGetShaderiv(value, GL_COMPILE_STATUS, &valid);
  if (!valid) { char log[4096]; glGetShaderInfoLog(value, sizeof(log), nullptr, log); throw std::runtime_error(log); }
  return value;
}
int main() {
  try {
    if (SDL_Init(SDL_INIT_VIDEO)) throw std::runtime_error(SDL_GetError());
    SDL_GL_SetAttribute(SDL_GL_CONTEXT_MAJOR_VERSION, 3);
    SDL_GL_SetAttribute(SDL_GL_CONTEXT_MINOR_VERSION, 3);
    SDL_GL_SetAttribute(SDL_GL_CONTEXT_PROFILE_MASK, SDL_GL_CONTEXT_PROFILE_CORE);
    auto window = SDL_CreateWindow("Grid verification", 0, 0, 220, 220, SDL_WINDOW_OPENGL | SDL_WINDOW_HIDDEN);
    if (!window) throw std::runtime_error(SDL_GetError());
    auto context = SDL_GL_CreateContext(window);
    if (!context || !gladLoadGLLoader(SDL_GL_GetProcAddress)) throw std::runtime_error("OpenGL unavailable");
    auto vs = shader(GL_VERTEX_SHADER, resource::data_shader_default_vs);
    auto fs = shader(GL_FRAGMENT_SHADER, resource::data_shader_bed_fs);
    auto program = glCreateProgram(); glAttachShader(program, vs); glAttachShader(program, fs); glLinkProgram(program);
    GLint linked; glGetProgramiv(program, GL_LINK_STATUS, &linked);
    if (!linked) throw std::runtime_error("Bed shader linking failed");
    glUseProgram(program);
    // Top view: one pixel per millimetre, using the real bed coordinate system.
    float transform[16]{};
    transform[0] = 2.f/220; transform[9] = -2.f/220;
    transform[12] = transform[13] = -1; transform[15] = 1;
    glUniformMatrix4fv(glGetUniformLocation(program, "u_mvp"), 1, GL_FALSE, transform);
    const float positions[] = {0,0,0, 220,0,0, 0,0,-220, 220,0,0, 220,0,-220, 0,0,-220};
    GLuint vao, buffer; glGenVertexArrays(1, &vao); glBindVertexArray(vao);
    glGenBuffers(1, &buffer); glBindBuffer(GL_ARRAY_BUFFER, buffer);
    glBufferData(GL_ARRAY_BUFFER, sizeof(positions), positions, GL_STATIC_DRAW);
    glEnableVertexAttribArray(0); glVertexAttribPointer(0, 3, GL_FLOAT, GL_FALSE, 0, nullptr);
    glVertexAttrib3f(1, 0, 1, 0); glVertexAttrib4f(2, .5f, .5f, .5f, 1);
    glViewport(0, 0, 220, 220); glDrawArrays(GL_TRIANGLES, 0, 6); glFinish();
    auto red = [](int x, int y) { unsigned char pixel[4]; glReadPixels(x,y,1,1,GL_RGBA,GL_UNSIGNED_BYTE,pixel); return int(pixel[0]); };
    const int plain = red(15,105), minor = red(10,105), next = red(20,105), major = red(50,105);
    if (!(minor < plain - 20 && next < plain - 20 && major < minor)) throw std::runtime_error("Grid pixel spacing/contrast failed");
    glDeleteBuffers(1, &buffer); glDeleteVertexArrays(1, &vao);
    glDeleteProgram(program); glDeleteShader(vs); glDeleteShader(fs);
    SDL_GL_DeleteContext(context); SDL_DestroyWindow(window); SDL_Quit();
    std::cout << "PASS: OpenGL bed shader, 10 mm grid spacing, stronger 50 mm lines\n";
    return 0;
  } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
