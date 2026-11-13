#pragma once
#include <map>
#include <string>
#include <fstream>
#include <sstream>
#include <iomanip>
#include <filesystem>
#include <cmath>
#include <type_traits>

class Preferences {
public:
  std::map<std::string, std::string> values;
  void load(const std::string& path) {
    values.clear();
    std::ifstream file(path);
    for (std::string line; std::getline(file, line);) {
      const auto split = line.find('=');
      if (split != std::string::npos) values[line.substr(0, split)] = line.substr(split + 1);
    }
  }
  template<class T> T get(const std::string& key, T fallback) const {
    auto it = values.find(key); if (it == values.end()) return fallback;
    T result{}; std::istringstream input(it->second);
    if (!(input >> result) || !(input >> std::ws).eof()) return fallback;
    if constexpr (std::is_floating_point_v<T>) if (!std::isfinite(result)) return fallback;
    return result;
  }
  template<class T> void set(const std::string& key, T value) {
    std::ostringstream text; text << std::setprecision(10) << value; values[key] = text.str();
  }
  std::string serialize() const {
    std::ostringstream text;
    for (const auto& item : values) text << item.first << '=' << item.second << '\n';
    return text.str();
  }
  bool save(const std::string& path) const {
    { std::ofstream output(path + ".tmp", std::ios::trunc); output << serialize(); if (!output) return false; }
    std::error_code error;
    std::filesystem::rename(path + ".tmp", path, error);
    if (error) {
      std::filesystem::copy_file(path + ".tmp", path, std::filesystem::copy_options::overwrite_existing, error);
      if (!error) std::filesystem::remove(path + ".tmp", error);
    }
    return !error;
  }
};
