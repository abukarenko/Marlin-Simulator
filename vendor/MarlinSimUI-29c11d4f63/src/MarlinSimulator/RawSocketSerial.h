// Loopback-only byte transport for MarlinSimUI on Windows.
// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#pragma push_macro("ABC")
#undef ABC
#include <winsock2.h>
#include <ws2tcpip.h>
#pragma pop_macro("ABC")
#undef ERROR
#include <SDL2/SDL_net.h>
#include <atomic>
#include <deque>
#include <mutex>
#include <thread>
#include <stdexcept>
#include <algorithm>

class RawSocketSerial {
  SOCKET listener = INVALID_SOCKET;
  std::atomic_bool active{false};
  std::thread worker;
  std::recursive_mutex mutex;
  std::deque<uint8_t> rx, tx;
  static constexpr size_t capacity = 131072;

  void execute() {
    SOCKET peer = INVALID_SOCKET;
    uint8_t buffer[4096];
    while (active) {
      fd_set readers, writers;
      FD_ZERO(&readers); FD_ZERO(&writers);
      FD_SET(listener, &readers);
      if (peer != INVALID_SOCKET) {
        std::scoped_lock lock(mutex);
        if (rx.size() < capacity) FD_SET(peer, &readers);
        if (!tx.empty()) FD_SET(peer, &writers);
      }
      timeval timeout{0, 5000};
      if (select(0, &readers, &writers, nullptr, &timeout) == SOCKET_ERROR) break;
      if (FD_ISSET(listener, &readers)) {
        SOCKET incoming = accept(listener, nullptr, nullptr);
        if (incoming != INVALID_SOCKET) {
          if (peer != INVALID_SOCKET) closesocket(incoming);
          else {
            u_long nonblocking = 1;
            ioctlsocket(incoming, FIONBIO, &nonblocking);
            BOOL no_delay = TRUE;
            setsockopt(incoming, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<char*>(&no_delay), sizeof(no_delay));
            peer = incoming;
            std::scoped_lock lock(mutex);
            rx.clear(); tx.clear();
          }
        }
      }
      if (peer != INVALID_SOCKET && FD_ISSET(peer, &readers)) {
        int free;
        { std::scoped_lock lock(mutex); free = static_cast<int>(std::min(sizeof(buffer), capacity - rx.size())); }
        int count = recv(peer, reinterpret_cast<char*>(buffer), free, 0);
        if (count > 0) { std::scoped_lock lock(mutex); rx.insert(rx.end(), buffer, buffer + count); }
        else if (count == 0 || WSAGetLastError() != WSAEWOULDBLOCK) {
          closesocket(peer); peer = INVALID_SOCKET;
          std::scoped_lock lock(mutex); rx.clear(); tx.clear();
        }
      }
      if (peer != INVALID_SOCKET && FD_ISSET(peer, &writers)) {
        std::scoped_lock lock(mutex);
        int size = static_cast<int>(std::min(sizeof(buffer), tx.size()));
        std::copy_n(tx.begin(), size, buffer);
        int count = ::send(peer, reinterpret_cast<char*>(buffer), size, 0);
        if (count > 0) tx.erase(tx.begin(), tx.begin() + count);
        else if (count == 0 || WSAGetLastError() != WSAEWOULDBLOCK) {
          closesocket(peer); peer = INVALID_SOCKET; rx.clear(); tx.clear();
        }
      }
      // No host is connected: do not retain old acknowledgements for a new host.
      if (peer == INVALID_SOCKET) { std::scoped_lock lock(mutex); tx.clear(); }
    }
    if (peer != INVALID_SOCKET) closesocket(peer);
  }

public:
  std::recursive_mutex& buffer_mutex = mutex;
  void listen_on_port(uint16_t port) {
    WSADATA data;
    if (WSAStartup(MAKEWORD(2, 2), &data)) throw std::runtime_error("WSAStartup failed");
    listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = htons(port);
    if (listener == INVALID_SOCKET || bind(listener, reinterpret_cast<sockaddr*>(&address), sizeof(address)) || listen(listener, 1)) {
      if (listener != INVALID_SOCKET) closesocket(listener);
      listener = INVALID_SOCKET; WSACleanup();
      throw std::runtime_error("Cannot listen on 127.0.0.1:8099; another simulator may be running");
    }
    active = true;
    worker = std::thread(&RawSocketSerial::execute, this);
  }
  void stop() {
    if (!active.exchange(false)) return;
    if (worker.joinable()) worker.join();
    closesocket(listener); listener = INVALID_SOCKET;
    WSACleanup();
  }
  ~RawSocketSerial() { stop(); }
  size_t available() { std::scoped_lock lock(mutex); return rx.size(); }
  int16_t read() {
    std::scoped_lock lock(mutex);
    if (rx.empty()) return -1;
    auto value = rx.front(); rx.pop_front(); return value;
  }
  size_t readBytes(char* destination, size_t length) {
    std::scoped_lock lock(mutex);
    length = std::min(length, rx.size());
    std::copy_n(rx.begin(), length, destination);
    rx.erase(rx.begin(), rx.begin() + length);
    return length;
  }
  size_t write(char value) { write(reinterpret_cast<const uint8_t*>(&value), 1); return 1; }
  void write(const char* value) { while (*value) write(*value++); }
  void write(const uint8_t* source, size_t length) {
    size_t offset = 0;
    while (offset < length && active) {
      {
        std::scoped_lock lock(mutex);
        size_t count = std::min(length - offset, capacity - tx.size());
        tx.insert(tx.end(), source + offset, source + offset + count);
        offset += count;
      }
      if (offset < length) std::this_thread::yield();
    }
  }
};
