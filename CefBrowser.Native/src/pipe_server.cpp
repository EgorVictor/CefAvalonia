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

static void DebugLog(const char* msg) {
    OutputDebugStringA("[PipeServer] ");
    OutputDebugStringA(msg);
    OutputDebugStringA("\n");
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
    DebugLog("Start called");
    running_ = true;
    writer_running_ = true;
    write_event_ = CreateEventW(nullptr, FALSE, FALSE, nullptr); // auto-reset
    writer_thread_ = std::thread(&PipeServer::WriterThreadProc, this);
    thread_ = std::thread(&PipeServer::ThreadProc, this,
                          std::move(onCommand), std::move(onResize),
                          std::move(onDisconnect), std::move(onConnected));
    DebugLog("Thread launched");
}

void PipeServer::Stop()
{
    DebugLog("Stop called");
    writer_running_ = false;
    running_ = false;
    if (write_event_) SetEvent(write_event_);   // unblock writer
    if (stop_event_) SetEvent(stop_event_);       // unblock pipe reader
    // Close pipe handle to break pending I/O
    if (pipe_ != INVALID_HANDLE_VALUE) {
        CloseHandle(pipe_);
        pipe_ = INVALID_HANDLE_VALUE;
    }
    if (writer_thread_.joinable()) {
        DebugLog("Joining writer thread...");
        writer_thread_.join();
        DebugLog("Writer thread joined");
    }
    if (thread_.joinable()) {
        DebugLog("Joining pipe thread...");
        thread_.join();
        DebugLog("Pipe thread joined");
    }
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
    char tmp[256];
    DebugLog("ThreadProc started");

    HANDLE hostProcess = nullptr;
    if (host_pid_ > 0) {
        hostProcess = OpenProcess(SYNCHRONIZE, FALSE, host_pid_);
        sprintf_s(tmp, "hostProcess=0x%IX", (size_t)hostProcess);
        DebugLog(tmp);
    }

    std::wstring fullPath = MakePipePath(pipe_name_);
    sprintf_s(tmp, "Creating pipe: %S", fullPath.c_str());
    DebugLog(tmp);

    // Synchronous pipe - no FILE_FLAG_OVERLAPPED
    pipe_ = CreateNamedPipeW(fullPath.c_str(),
                             PIPE_ACCESS_DUPLEX,
                             PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
                             1, 4096, 4096, 0, nullptr);
    sprintf_s(tmp, "CreateNamedPipe result=0x%IX err=%d", (size_t)pipe_, GetLastError());
    DebugLog(tmp);

    if (pipe_ == INVALID_HANDLE_VALUE) {
        DebugLog("CreateNamedPipe FAILED - exiting thread");
        if (hostProcess) CloseHandle(hostProcess);
        return;
    }

    // Wait for client connection (blocks until client connects)
    DebugLog("ConnectNamedPipe (blocking)...");
    BOOL connected = ConnectNamedPipe(pipe_, nullptr);
    DWORD lastErr = GetLastError();
    sprintf_s(tmp, "ConnectNamedPipe result=%d err=%d", connected, lastErr);
    DebugLog(tmp);

    if (connected == 0) {
        // Failed to connect - check if Stop() was called
        if (!running_) {
            DebugLog("ConnectNamedPipe failed because Stop() was called");
            CloseHandle(pipe_);
            pipe_ = INVALID_HANDLE_VALUE;
            if (hostProcess) CloseHandle(hostProcess);
            return;
        }
        // Handle ERROR_PIPE_CONNECTED (client already connected)
        if (lastErr == ERROR_PIPE_CONNECTED) {
            DebugLog("Client already connected (ERROR_PIPE_CONNECTED)");
        } else {
            sprintf_s(tmp, "ConnectNamedPipe FAILED err=%d", lastErr);
            DebugLog(tmp);
            CloseHandle(pipe_);
            pipe_ = INVALID_HANDLE_VALUE;
            if (hostProcess) CloseHandle(hostProcess);
            return;
        }
    }

    DebugLog("Client connected! Calling onConnected...");
    if (onConnected) onConnected();
    DebugLog("onConnected done, entering read loop");

    // Main read loop
    while (running_) {
        std::string line;
        if (!ReadLine(line)) {
            DebugLog("ReadLine returned false");
            break;
        }
        if (line.empty()) {
            DebugLog("Empty line, breaking");
            break;
        }

        sprintf_s(tmp, "Received: %s", line.c_str());
        DebugLog(tmp);

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
                } catch (const std::exception& e) {
                    sprintf_s(tmp, "Resize parse failed: %s", e.what());
                    DebugLog(tmp);
                }
            }
        } else {
            if (onCommand) onCommand(cmd, arg);
        }
    }

    DebugLog("Read loop ended");
    if (onDisconnect) onDisconnect();

    // Cleanup only if Stop() hasn't already closed pipe_
    if (pipe_ != INVALID_HANDLE_VALUE) {
        DebugLog("Disconnecting and closing pipe");
        DisconnectNamedPipe(pipe_);
        CloseHandle(pipe_);
        pipe_ = INVALID_HANDLE_VALUE;
    }

    if (hostProcess) CloseHandle(hostProcess);
    DebugLog("ThreadProc exiting");
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
                // Message is larger than buffer - append what we got
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

        // Check for newline at end of message
        if (!line.empty() && line.back() == '\n') {
            if (line.size() >= 2 && line[line.size() - 2] == '\r')
                line.resize(line.size() - 2);
            else
                line.resize(line.size() - 1);
            return true;
        }

        // If we got less than buffer, this was the complete message (no newline?)
        if (bytesRead < sizeof(buf)) return !line.empty();
    }
    return false;
}

void PipeServer::WriterThreadProc()
{
    char tmp[256];
    DebugLog("WriterThreadProc started");
    while (writer_running_) {
        WaitForSingleObject(write_event_, INFINITE);
        if (!writer_running_) break;

        // Drain all queued messages
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
            if (!WriteFile(pipe_, msg.data(), (DWORD)msg.size(), &written, nullptr)) {
                DWORD err = GetLastError();
                sprintf_s(tmp, "Writer: WriteFile failed, err=%d (%s)",
                          err, (err == ERROR_BROKEN_PIPE) ? "BROKEN_PIPE" :
                               (err == ERROR_PIPE_NOT_CONNECTED) ? "NOT_CONNECTED" : "OTHER");
                DebugLog(tmp);
            }
        }
    }
    DebugLog("WriterThreadProc exiting");
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
