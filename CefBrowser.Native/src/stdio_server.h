#pragma once

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <functional>
#include <mutex>
#include <string>
#include <thread>

// Callback invoked for every complete line received from the managed host.
using StdioCommandCallback = std::function<void(const std::string& cmd, const std::string& arg)>;

// Line-oriented command/event transport over stdin/stdout.
//
// The reader and writer are fully decoupled: the reader only parses lines and
// invokes the command callback, the writer owns stdout and drains an internal
// queue. SendEvent() may therefore be called from any thread (CEF UI thread,
// IO thread, ...) without blocking on the pipe.
class StdioServer {
public:
    StdioServer() = default;
    ~StdioServer();

    StdioServer(const StdioServer&) = delete;
    StdioServer& operator=(const StdioServer&) = delete;

    void Start(StdioCommandCallback onCommand,
               std::function<void()> onDisconnect,
               std::function<void()> onConnected);
    void Stop();
    void SendEvent(const std::string& message);

    // Number of events dropped because the managed side stopped reading.
    uint64_t DroppedEventCount() const { return dropped_.load(); }

private:
    void ReaderThreadProc(StdioCommandCallback onCommand,
                          std::function<void()> onDisconnect,
                          std::function<void()> onConnected);
    void WriterThreadProc();
    void HandleLine(const std::string& line, const StdioCommandCallback& onCommand);

    std::thread reader_thread_;
    std::thread writer_thread_;

    std::deque<std::string> write_queue_;
    mutable std::mutex write_mutex_;
    std::condition_variable write_cv_;

    std::atomic<bool> running_{false};
    std::atomic<bool> writer_running_{false};
    std::atomic<uint64_t> dropped_{0};

    static constexpr size_t kMaxQueuedEvents = 8192;

#if defined(__linux__)
    int wake_read_fd_ = -1;
    int wake_write_fd_ = -1;
#endif
};
