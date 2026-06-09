#pragma once
#include <string>
#include <thread>
#include <queue>
#include <functional>
#include <mutex>
#include <iostream>

using StdioCommandCallback = std::function<void(const std::string& cmd, const std::string& arg)>;

class StdioServer {
public:
    StdioServer();
    ~StdioServer();

    void Start(StdioCommandCallback onCommand, std::function<void(int, int)> onResize,
               std::function<void()> onDisconnect, std::function<void()> onConnected);
    void Stop();
    void SendEvent(const std::string& message);

private:
    void ReaderThreadProc(StdioCommandCallback onCommand, std::function<void(int, int)> onResize,
                          std::function<void()> onDisconnect, std::function<void()> onConnected);
    void WriterThreadProc();

    std::thread reader_thread_;
    std::thread writer_thread_;
    std::queue<std::string> write_queue_;
    std::mutex write_queue_mutex_;
    bool running_ = false;
    bool writer_running_ = false;
    void* write_event_ = nullptr;  // Will be replaced with actual event handle if needed
};
