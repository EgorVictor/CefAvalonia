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

void StdioServer::Start(StdioCommandCallback onCommand, std::function<void(int, int)> onResize,
                        std::function<void()> onDisconnect, std::function<void()> onConnected)
{
    running_ = true;
    writer_running_ = true;

    reader_thread_ = std::thread(&StdioServer::ReaderThreadProc, this,
                                 std::move(onCommand), std::move(onResize),
                                 std::move(onDisconnect), std::move(onConnected));
    writer_thread_ = std::thread(&StdioServer::WriterThreadProc, this);
}

void StdioServer::Stop()
{
    running_ = false;
    writer_running_ = false;

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

void StdioServer::ReaderThreadProc(StdioCommandCallback onCommand, std::function<void(int, int)> onResize,
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

        fprintf(stderr, "DIAG: [C++] Recv cmd=%s arg=%s\n", cmd.c_str(), arg.c_str()); fflush(stderr);

        if (cmd == "Resize" && onResize)
        {
            // Parse "w|h" from arg
            auto sep2 = arg.find('|');
            if (sep2 != std::string::npos)
            {
                int w = std::stoi(arg.substr(0, sep2));
                int h = std::stoi(arg.substr(sep2 + 1));
                onResize(w, h);
            }
        }
        else if (onCommand)
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
