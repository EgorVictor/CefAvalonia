#include "pipe_server.h"
#include <cstdio>
#include <vector>
#include <cassert>

static std::wstring Utf8ToWide(const std::string& str)
{
    if (str.empty()) return {};
    int len = MultiByteToWideChar(CP_UTF8, 0, str.data(), (int)str.size(), nullptr, 0);
    std::wstring result(len, 0);
    MultiByteToWideChar(CP_UTF8, 0, str.data(), (int)str.size(), &result[0], len);
    return result;
}

PipeServer::PipeServer(const std::string& pipeName, int hostPid)
    : pipe_name_(pipeName), host_pid_(hostPid)
{
    stop_event_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    assert(stop_event_);
}

PipeServer::~PipeServer()
{
    Stop();
    if (stop_event_) CloseHandle(stop_event_);
}

void PipeServer::Start(PipeCommandCallback onCommand,
                       std::function<void(int, int)> onResize,
                       std::function<void()> onDisconnect,
                       std::function<void()> onConnected)
{
    running_ = true;
    writer_running_ = true;
    write_event_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    writer_thread_ = std::thread(&PipeServer::WriterThreadProc, this);
    thread_ = std::thread(&PipeServer::ThreadProc, this,
                          std::move(onCommand), std::move(onResize),
                          std::move(onDisconnect), std::move(onConnected));
}

void PipeServer::Stop()
{
    writer_running_ = false;
    running_ = false;
    if (write_event_) SetEvent(write_event_);
    if (stop_event_) SetEvent(stop_event_);
    if (pipe_ != INVALID_HANDLE_VALUE) {
        CloseHandle(pipe_);
        pipe_ = INVALID_HANDLE_VALUE;
    }
    if (writer_thread_.joinable())
        writer_thread_.join();
    if (thread_.joinable())
        thread_.join();
    if (write_event_) {
        CloseHandle(write_event_);
        write_event_ = nullptr;
    }
}

static std::wstring MakePipePath(const std::string& name)
{
    return Utf8ToWide("\\\\.\\pipe\\" + name);
}

void PipeServer::ThreadProc(PipeCommandCallback onCommand,
                            std::function<void(int, int)> onResize,
                            std::function<void()> onDisconnect,
                            std::function<void()> onConnected)
{
    HANDLE hostProcess = nullptr;
    if (host_pid_ > 0)
        hostProcess = OpenProcess(SYNCHRONIZE, FALSE, host_pid_);

    std::wstring fullPath = MakePipePath(pipe_name_);

    pipe_ = CreateNamedPipeW(fullPath.c_str(),
                             PIPE_ACCESS_DUPLEX,
                             PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
                             1, 4096, 4096, 0, nullptr);

    if (pipe_ == INVALID_HANDLE_VALUE) {
        if (hostProcess) CloseHandle(hostProcess);
        return;
    }

    BOOL connected = ConnectNamedPipe(pipe_, nullptr);
    DWORD lastErr = GetLastError();

    if (connected == 0) {
        if (!running_) {
            CloseHandle(pipe_);
            pipe_ = INVALID_HANDLE_VALUE;
            if (hostProcess) CloseHandle(hostProcess);
            return;
        }
        if (lastErr == ERROR_PIPE_CONNECTED) {
            // Client already connected
        } else {
            CloseHandle(pipe_);
            pipe_ = INVALID_HANDLE_VALUE;
            if (hostProcess) CloseHandle(hostProcess);
            return;
        }
    }

    if (onConnected) onConnected();

    while (running_) {
        std::string line;
        if (!ReadLine(line))
            break;
        if (line.empty())
            break;

        auto sep = line.find('|');
        std::string cmd = (sep != std::string::npos) ? line.substr(0, sep) : line;
        std::string arg = (sep != std::string::npos) ? line.substr(sep + 1) : "";

        if (cmd == "Resize") {
            auto p = arg.find('|');
            if (p != std::string::npos) {
                try {
                    int w = std::stoi(arg.substr(0, p));
                    int h = std::stoi(arg.substr(p + 1));
                    if (w > 0 && h > 0 && onResize) onResize(w, h);
                } catch (...) { }
            }
        } else {
            if (onCommand) onCommand(cmd, arg);
        }
    }

    if (onDisconnect) onDisconnect();

    if (pipe_ != INVALID_HANDLE_VALUE) {
        DisconnectNamedPipe(pipe_);
        CloseHandle(pipe_);
        pipe_ = INVALID_HANDLE_VALUE;
    }

    if (hostProcess) CloseHandle(hostProcess);
}

bool PipeServer::ReadLine(std::string& line)
{
    line.clear();
    char buf[256];
    while (running_) {
        DWORD bytesRead = 0;
        if (!ReadFile(pipe_, buf, sizeof(buf), &bytesRead, nullptr)) {
            DWORD err = GetLastError();
            if (err == ERROR_MORE_DATA) {
                line.append(buf, bytesRead);
                continue;
            }
            if (err == ERROR_BROKEN_PIPE || err == ERROR_PIPE_NOT_CONNECTED)
                return false;
            if (err == ERROR_NO_DATA) return false;
            return false;
        }
        if (bytesRead == 0) return false;
        line.append(buf, bytesRead);

        if (!line.empty() && line.back() == '\n') {
            if (line.size() >= 2 && line[line.size() - 2] == '\r')
                line.resize(line.size() - 2);
            else
                line.resize(line.size() - 1);
            return true;
        }

        if (bytesRead < sizeof(buf)) return !line.empty();
    }
    return false;
}

void PipeServer::WriterThreadProc()
{
    while (writer_running_) {
        WaitForSingleObject(write_event_, INFINITE);
        if (!writer_running_) break;

        for (;;) {
            std::string msg;
            {
                std::lock_guard<std::mutex> lock(write_queue_mutex_);
                if (write_queue_.empty()) break;
                msg = write_queue_.front();
                write_queue_.pop();
            }
            msg += '\n';
            DWORD written = 0;
            WriteFile(pipe_, msg.data(), (DWORD)msg.size(), &written, nullptr);
        }
    }
}

void PipeServer::SendEvent(const std::string& message)
{
    if (!writer_running_) return;
    {
        std::lock_guard<std::mutex> lock(write_queue_mutex_);
        write_queue_.push(message);
    }
    if (write_event_) SetEvent(write_event_);
}
