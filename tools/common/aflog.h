// AirFrameXR in-process log writer for the C++ programs (mirrordesktop, the shim's fallback): one bounded queue, one writer thread.
// Same rules as the host's logsink.rs (docs/ARCHITECTURE.md, "Logging"): callers format the line, queue it and return, they never wait for the
// disk; a full queue drops the line and the writer says how many once it has caught up; the thread batches (flush every 200 ms, at once for
// warnings and errors); `flush` waits (2 s at most) for what is queued. Line format: "yyyy-mm-dd hh:mm:ss.mmm [component] LEVEL message".
// Header only, Windows + C++17.
#pragma once
#include <windows.h>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include <algorithm>

namespace aflog {

enum Level { Error = 0, Warn = 1, Info = 2, Verbose = 3, Debug = 4 };

inline const char* levelName(Level l) {
    static const char* n[] = { "ERROR", "WARN ", "INFO ", "VERB ", "DEBUG" };
    return n[(int)l < 0 ? 0 : (int)l > 4 ? 4 : (int)l];
}
inline bool parseLevel(const char* s, Level& out) {
    static const char* n[] = { "error", "warn", "info", "verbose", "debug" };
    for (int i = 0; i < 5; i++) if (_stricmp(s, n[i]) == 0) { out = (Level)i; return true; }
    return false;
}

inline std::string stamp() {
    SYSTEMTIME t; GetLocalTime(&t);
    char b[40]; snprintf(b, sizeof b, "%04d-%02d-%02d %02d:%02d:%02d.%03d", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    return b;
}
inline std::string compactStamp() {
    SYSTEMTIME t; GetLocalTime(&t);
    char b[24]; snprintf(b, sizeof b, "%04d%02d%02d-%02d%02d%02d", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
    return b;
}

// Keeps the newest `keep` "*.log" files of `dir` (the names are start times: they sort).
inline void pruneRuns(const std::string& dir, size_t keep) {
    std::vector<std::string> names;
    WIN32_FIND_DATAA f; HANDLE h = FindFirstFileA((dir + "\\*.log").c_str(), &f);
    if (h == INVALID_HANDLE_VALUE) return;
    do { if (!(f.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) names.push_back(f.cFileName); } while (FindNextFileA(h, &f));
    FindClose(h);
    std::sort(names.begin(), names.end());
    for (size_t i = 0; i + keep < names.size(); i++) DeleteFileA((dir + "\\" + names[i]).c_str());
}

// Creates `dir` (one level under an existing folder) and returns "<dir>\<yyyymmdd-hhmmss>.log"; the older runs beyond `keep` are deleted.
inline std::string runFile(const std::string& dir, size_t keep = 20) {
    CreateDirectoryA(dir.c_str(), nullptr);
    pruneRuns(dir, keep);
    return dir + "\\" + compactStamp() + ".log";
}

class Writer {
public:
    ~Writer() { stop(); }
    // Opens `path` for appending and starts the thread. False when the file cannot be opened (the logging then does nothing).
    bool start(const std::string& path, const char* component, Level level = Info, size_t queueMax = 4096) {
        file_ = _fsopen(path.c_str(), "ab", _SH_DENYNO);   // shared: the analysis reads it while the program runs
        if (!file_) return false;
        comp_ = component; level_ = level; max_ = queueMax; stop_ = false;
        th_ = std::thread([this] { run(); });
        return true;
    }
    void setLevel(Level l) { level_ = l; }
    bool enabled(Level l) const { return file_ && (int)l <= (int)level_.load(); }
    uint64_t dropped() const { return dropped_.load(); }

    void log(Level l, const char* fmt, ...) {
        if (!enabled(l)) return;
        char msg[1024];
        va_list a; va_start(a, fmt); vsnprintf(msg, sizeof msg, fmt, a); va_end(a);
        std::string line = stamp() + " [" + comp_ + "] " + levelName(l) + " " + msg + "\n";
        {
            std::lock_guard<std::mutex> g(m_);
            if (q_.size() >= max_) { dropped_++; return; }
            q_.push_back(std::move(line)); submitted_++;
            if (l <= Warn) urgent_ = true;
        }
        cv_.notify_one();
    }

    // Waits (2 s at most) until everything queued so far is written and flushed.
    void flush() {
        if (!file_) return;
        std::unique_lock<std::mutex> g(m_);
        uint64_t want = submitted_;
        flushReq_ = true; cv_.notify_one();
        done_.wait_for(g, std::chrono::seconds(2), [&] { return written_ >= want; });
    }

    void stop() {
        if (!th_.joinable()) return;
        { std::lock_guard<std::mutex> g(m_); stop_ = true; }
        cv_.notify_one();
        th_.join();
        if (file_) { fclose(file_); file_ = nullptr; }
    }

private:
    void run() {
        uint64_t reported = 0;
        for (;;) {
            std::deque<std::string> batch; bool quit;
            {
                std::unique_lock<std::mutex> g(m_);
                cv_.wait_for(g, std::chrono::milliseconds(200), [&] { return stop_ || urgent_ || flushReq_ || !q_.empty(); });
                batch.swap(q_); quit = stop_; urgent_ = false; flushReq_ = false;
            }
            for (auto& s : batch) fwrite(s.data(), 1, s.size(), file_);
            uint64_t d = dropped_.load();
            if (d > reported) {
                char b[160]; snprintf(b, sizeof b, "%s [%s] WARN  %llu log lines dropped (queue full)\n", stamp().c_str(), comp_.c_str(), (unsigned long long)(d - reported));
                fputs(b, file_); reported = d;
            }
            fflush(file_);
            { std::lock_guard<std::mutex> g(m_); written_ += batch.size(); }
            done_.notify_all();
            if (quit) return;
        }
    }

    FILE* file_ = nullptr;
    std::string comp_;
    std::atomic<int> level_{ Info };
    size_t max_ = 4096;
    std::mutex m_;
    std::condition_variable cv_, done_;
    std::deque<std::string> q_;
    uint64_t submitted_ = 0, written_ = 0;
    std::atomic<uint64_t> dropped_{ 0 };
    bool stop_ = false, urgent_ = false, flushReq_ = false;
    std::thread th_;
};

} // namespace aflog
