#include "stdio_server.h"

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>

#if defined(_WIN32)
#include <windows.h>
#elif defined(__linux__)
#include <cerrno>
#include <poll.h>
#include <unistd.h>
#endif

namespace {

bool DiagEnabled()
{
    static const bool enabled = [] {
        const char* value = std::getenv("CEF_DIAG");
        return value && value[0] == '1';
    }();
    return enabled;
}

void DiagLog(const char* message, const std::string& detail)
{
    if (!DiagEnabled())
        return;

    fprintf(stderr, "DIAG: [C++] %s%s\n", message, detail.c_str());
    fflush(stderr);
}

} // namespace

StdioServer::~StdioServer()
{
    Stop();
}

void StdioServer::Start(StdioCommandCallback onCommand,
                        std::function<void()> onDisconnect,
                        std::function<void()> onConnected)
{
#if defined(__linux__)
    if (wake_read_fd_ < 0) {
        int fds[2] = {-1, -1};
        if (pipe(fds) == 0) {
            wake_read_fd_ = fds[0];
            wake_write_fd_ = fds[1];
        }
    }
#endif

    running_ = true;
    writer_running_ = true;

    reader_thread_ = std::thread(&StdioServer::ReaderThreadProc, this,
                                 std::move(onCommand),
                                 std::move(onDisconnect), std::move(onConnected));
    writer_thread_ = std::thread(&StdioServer::WriterThreadProc, this);
}

void StdioServer::Stop()
{
    running_ = false;

    {
        std::lock_guard<std::mutex> lock(write_mutex_);
        writer_running_ = false;
    }
    write_cv_.notify_all();

#if defined(_WIN32)
    // Cancel the blocking ReadFile() issued by the reader thread. CancelIoEx
    // alone does not reliably interrupt a synchronous read from another thread.
    if (reader_thread_.joinable())
        CancelSynchronousIo(reader_thread_.native_handle());
#elif defined(__linux__)
    // Unblock poll()/read() in the reader thread with a self-pipe.
    if (wake_write_fd_ >= 0) {
        char c = 'x';
        ssize_t ignored = write(wake_write_fd_, &c, 1);
        (void)ignored;
    }
#endif

    if (reader_thread_.joinable())
        reader_thread_.join();
    if (writer_thread_.joinable())
        writer_thread_.join();

#if defined(__linux__)
    if (wake_read_fd_ >= 0) {
        close(wake_read_fd_);
        wake_read_fd_ = -1;
    }
    if (wake_write_fd_ >= 0) {
        close(wake_write_fd_);
        wake_write_fd_ = -1;
    }
#endif
}

void StdioServer::SendEvent(const std::string& message)
{
    {
        std::lock_guard<std::mutex> lock(write_mutex_);
        if (!writer_running_)
            return;

        if (write_queue_.size() >= kMaxQueuedEvents) {
            write_queue_.pop_front();
            uint64_t dropped = ++dropped_;
            if (DiagEnabled() && (dropped == 1 || dropped % 1000 == 0))
                fprintf(stderr, "DIAG: [C++] event queue full, dropped=%llu\n",
                        (unsigned long long)dropped);
        }

        write_queue_.push_back(message);
    }

    write_cv_.notify_one();
}

void StdioServer::HandleLine(const std::string& line, const StdioCommandCallback& onCommand)
{
    if (line.empty())
        return;

    size_t sep = line.find('|');
    std::string cmd = (sep != std::string::npos) ? line.substr(0, sep) : line;
    std::string arg = (sep != std::string::npos) ? line.substr(sep + 1) : "";

    DiagLog("Recv cmd=", cmd + " arg=" + arg);

    if (onCommand)
        onCommand(cmd, arg);
}

void StdioServer::ReaderThreadProc(StdioCommandCallback onCommand,
                                   std::function<void()> onDisconnect,
                                   std::function<void()> onConnected)
{
    if (onConnected)
        onConnected();

    std::string pending;

#if defined(_WIN32)
    HANDLE stdIn = GetStdHandle(STD_INPUT_HANDLE);
    if (stdIn == nullptr || stdIn == INVALID_HANDLE_VALUE) {
        if (onDisconnect) onDisconnect();
        return;
    }

    char buffer[4096];
    while (running_) {
        DWORD read = 0;
        BOOL ok = ReadFile(stdIn, buffer, sizeof(buffer), &read, nullptr);

        if (!ok) {
            DWORD error = GetLastError();
            if (error == ERROR_OPERATION_ABORTED || error == ERROR_BROKEN_PIPE ||
                error == ERROR_HANDLE_EOF || error == ERROR_INVALID_HANDLE)
                break;

            DiagLog("stdin read failed errno=", std::to_string(error));
            break;
        }

        if (read == 0)
            break;

        pending.append(buffer, read);

        size_t pos;
        while ((pos = pending.find('\n')) != std::string::npos) {
            std::string line = pending.substr(0, pos);
            pending.erase(0, pos + 1);
            if (!line.empty() && line.back() == '\r')
                line.pop_back();
            HandleLine(line, onCommand);
        }
    }
#elif defined(__linux__)
    char buffer[4096];
    while (running_) {
        struct pollfd fds[2];
        fds[0].fd = STDIN_FILENO;
        fds[0].events = POLLIN;
        fds[0].revents = 0;
        fds[1].fd = wake_read_fd_;
        fds[1].events = POLLIN;
        fds[1].revents = 0;

        int ready = poll(fds, 2, 500);
        if (ready < 0) {
            if (errno == EINTR)
                continue;
            break;
        }
        if (ready == 0)
            continue;
        if (fds[1].revents & POLLIN)
            break;
        if (!(fds[0].revents & (POLLIN | POLLHUP | POLLERR)))
            continue;

        ssize_t got = read(STDIN_FILENO, buffer, sizeof(buffer));
        if (got <= 0)
            break;

        pending.append(buffer, static_cast<size_t>(got));

        size_t pos;
        while ((pos = pending.find('\n')) != std::string::npos) {
            std::string line = pending.substr(0, pos);
            pending.erase(0, pos + 1);
            if (!line.empty() && line.back() == '\r')
                line.pop_back();
            HandleLine(line, onCommand);
        }
    }
#endif

    if (!pending.empty())
        HandleLine(pending, onCommand);

    running_ = false;

    if (onDisconnect)
        onDisconnect();
}

void StdioServer::WriterThreadProc()
{
    std::unique_lock<std::mutex> lock(write_mutex_);

    for (;;) {
        write_cv_.wait(lock, [this] {
            return !write_queue_.empty() || !writer_running_;
        });

        if (write_queue_.empty()) {
            if (!writer_running_)
                break;
            continue;
        }

        // Move the batch out of the shared queue and write it without holding
        // the lock, so SendEvent() from the CEF threads never blocks on I/O.
        std::deque<std::string> batch;
        batch.swap(write_queue_);
        lock.unlock();

        for (const std::string& message : batch) {
            fwrite(message.data(), 1, message.size(), stdout);
            fputc('\n', stdout);
        }
        fflush(stdout);

        lock.lock();
    }
}
