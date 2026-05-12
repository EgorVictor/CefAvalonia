#pragma once
#include <string>
#include <thread>
#include <queue>
#include <functional>
#include <mutex>
#include <Windows.h>
#include <WinBase.h>

using PipeCommandCallback = std::function<void(const std::string& cmd, const std::string& arg)>;

class PipeServer {
public:
    PipeServer(const std::string& pipeName, int hostPid);
    ~PipeServer();

    void Start(PipeCommandCallback onCommand, std::function<void(int, int)> onResize,
               std::function<void()> onDisconnect,
               std::function<void()> onConnected);
    void Stop();
    void SendEvent(const std::string& message);

private:
    void ThreadProc(PipeCommandCallback onCommand, std::function<void(int, int)> onResize,
                    std::function<void()> onDisconnect, std::function<void()> onConnected);
    void WriterThreadProc();
    bool ReadLine(std::string& line);

    std::string pipe_name_;
    int host_pid_;
    std::thread thread_;
    HANDLE pipe_ = INVALID_HANDLE_VALUE;
    HANDLE stop_event_ = nullptr;
    bool running_ = false;
    std::mutex write_mutex_;

    // Writer thread: SendEvent never blocks the caller (main thread)
    std::thread writer_thread_;
    std::queue<std::string> write_queue_;
    std::mutex write_queue_mutex_;
    HANDLE write_event_ = nullptr;
    bool writer_running_ = false;
};
