#include "stdio_server.h"
#include <iostream>
#include <cstdio>
#include <windows.h>

StdioServer::StdioServer() : running_(false), writer_running_(false)
{
}

StdioServer::~StdioServer()
{
    Stop();
}

void StdioServer::Start(StdioCommandCallback onCommand,
                        std::function<void()> onDisconnect, std::function<void()> onConnected)
{
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
    writer_running_ = false;

    // Unblock the reader thread if it is parked in getline(stdin): cancel the
    // pending read on the standard input handle. Without this, Stop() would
    // deadlock whenever the C# side keeps its stdin StreamWriter open (e.g.
    // Quit-after-last-browser path where the host process is still alive).
    HANDLE stdIn = GetStdHandle(STD_INPUT_HANDLE);
    if (stdIn && stdIn != INVALID_HANDLE_VALUE)
        CancelIoEx(stdIn, nullptr);

    if (reader_thread_.joinable())
        reader_thread_.join();
    if (writer_thread_.joinable())
        writer_thread_.join();
}

void StdioServer::SendEvent(const std::string& message)
{
    std::lock_guard<std::mutex> lock(write_queue_mutex_);
    write_queue_.push(message);
}

void StdioServer::ReaderThreadProc(StdioCommandCallback onCommand,
                                   std::function<void()> onDisconnect, std::function<void()> onConnected)
{
    if (onConnected) onConnected();

    std::string line;
    while (running_ && std::getline(std::cin, line))
    {
        if (line.empty()) continue;

        auto sep = line.find('|');
        std::string cmd = (sep != std::string::npos) ? line.substr(0, sep) : line;
        std::string arg = (sep != std::string::npos) ? line.substr(sep + 1) : "";

        {
            // DIAG logging is silent unless CEF_DIAG=1 (avoids per-message stderr flush cost)
            static const bool diagEnabled = [] {
                const char* e = std::getenv("CEF_DIAG");
                return e && e[0] == '1';
            }();
            if (diagEnabled) {
                fprintf(stderr, "DIAG: [C++] Recv cmd=%s arg=%s\n", cmd.c_str(), arg.c_str());
                fflush(stderr);
            }
        }

        // All commands (including Resize|id|w|h) flow through onCommand
        if (onCommand)
        {
            onCommand(cmd, arg);
        }
    }

    if (onDisconnect) onDisconnect();
}

void StdioServer::WriterThreadProc()
{
    while (writer_running_)
    {
        std::this_thread::sleep_for(std::chrono::milliseconds(10));

        std::lock_guard<std::mutex> lock(write_queue_mutex_);
        while (!write_queue_.empty())
        {
            auto msg = write_queue_.front();
            write_queue_.pop();
            std::cout << msg << std::endl;
            std::cout.flush();
        }
    }
}
