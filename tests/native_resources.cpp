#include "resources.h"
#include <fstream>
#include <stdexcept>
#include <iostream>

void check(bool valid) { if (!valid) throw std::runtime_error("File resource regression"); }
int main() {
  auto path = std::filesystem::temp_directory_path() / "marlin-emu-resource-regression.ini";
  { std::ofstream file(path, std::ios::binary); file << "[Window][Printer]\nPos=10,20\n"; }
  {
    resource::FileResource file(path);
    check(file.m_buffer == "[Window][Printer]\nPos=10,20\n");
    check(file.m_data.size() == file.m_buffer.size() + 1);
    check(file.m_data.back() == 0);
    { std::ofstream changed(path); changed << "short"; }
    file.load();
    check(file.m_buffer == "short");
    { std::ofstream empty(path); }
    file.load();
    check(file.m_buffer.empty() && file.m_data.size() == 1 && file.m_data[0] == 0);
  }
  std::filesystem::remove(path);
  std::cout << "PASS: saved resource, reload, empty resource, NUL terminator\n";
}
