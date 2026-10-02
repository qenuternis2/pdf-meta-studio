#include "fileutil.hpp"

#include "sha256.hpp"

#include <chrono>
#include <cstdio>
#include <fstream>
#include <random>
#include <vector>

#ifdef _WIN32
#include <windows.h>
#endif

namespace pm {

fs::path pathFromUtf8(const std::string& s) {
    return fs::path(std::u8string(reinterpret_cast<const char8_t*>(s.data()), s.size()));
}

std::string pathToUtf8(const fs::path& p) {
    auto u = p.u8string();
    return std::string(reinterpret_cast<const char*>(u.data()), u.size());
}

json Fingerprint::toJson() const {
    return json{{"size", size}, {"mtime", mtime}, {"sha256", sha256}};
}

Fingerprint Fingerprint::fromJson(const json& j) {
    Fingerprint f;
    f.size = j.at("size").get<std::uint64_t>();
    f.mtime = j.at("mtime").get<std::string>();
    f.sha256 = j.at("sha256").get<std::string>();
    return f;
}

Fingerprint computeFingerprint(const fs::path& p, Context* ctx) {
    Fingerprint f;
    std::error_code ec;
    f.size = fs::file_size(p, ec);
    if (ec) throw WorkerError("file_not_found", "Файл недоступен: " + pathToUtf8(p));
    f.mtime = std::to_string(fs::last_write_time(p, ec).time_since_epoch().count());
    std::ifstream in(p, std::ios::binary);
    if (!in) throw WorkerError("file_locked", "Не удалось открыть файл для чтения: " + pathToUtf8(p));
    Sha256 sha;
    std::vector<char> buf(1 << 20);
    std::uint64_t done = 0;
    while (in) {
        in.read(buf.data(), static_cast<std::streamsize>(buf.size()));
        auto n = in.gcount();
        if (n <= 0) break;
        sha.update(buf.data(), static_cast<size_t>(n));
        done += static_cast<std::uint64_t>(n);
        if (ctx) {
            ctx->checkCancel();
            if (f.size > 0) ctx->progress("hash", static_cast<int>(done * 100 / f.size));
        }
    }
    f.sha256 = sha.hexDigest();
    return f;
}

static std::string randomToken() {
    std::random_device rd;
    std::mt19937_64 gen((static_cast<std::uint64_t>(rd()) << 32) ^ rd() ^
                        static_cast<std::uint64_t>(std::chrono::steady_clock::now().time_since_epoch().count()));
    static const char* a = "abcdefghijklmnopqrstuvwxyz0123456789";
    std::string s;
    for (int i = 0; i < 10; ++i) s += a[gen() % 36];
    return s;
}

fs::path uniqueSibling(const fs::path& dir, const std::string& stemUtf8, const std::string& ext) {
    for (int i = 1; i < 10000; ++i) {
        std::string name = stemUtf8 + (i == 1 ? "" : " (" + std::to_string(i) + ")") + ext;
        fs::path candidate = dir / pathFromUtf8(name);
        std::error_code ec;
        if (!fs::exists(candidate, ec)) return candidate;
    }
    throw WorkerError("io_error", "Не удалось подобрать уникальное имя файла");
}

fs::path tempPathIn(const fs::path& dir) {
    for (int i = 0; i < 100; ++i) {
        fs::path candidate = dir / pathFromUtf8(".pdfmeta-" + randomToken() + ".tmp");
        std::error_code ec;
        if (!fs::exists(candidate, ec)) return candidate;
    }
    throw WorkerError("io_error", "Не удалось создать имя временного файла");
}

void replaceFile(const fs::path& from, const fs::path& to) {
#ifdef _WIN32
    if (!MoveFileExW(from.c_str(), to.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        DWORD err = GetLastError();
        if (err == ERROR_SHARING_VIOLATION || err == ERROR_LOCK_VIOLATION || err == ERROR_ACCESS_DENIED)
            throw WorkerError("file_locked", "Файл назначения занят другой программой или защищён от записи");
        throw WorkerError("io_error", "Не удалось переименовать временный файл (код " + std::to_string(err) + ")");
    }
#else
    std::error_code ec;
    fs::rename(from, to, ec);
    if (ec) throw WorkerError("io_error", "Не удалось переименовать временный файл: " + ec.message());
#endif
}

void copyFileExact(const fs::path& from, const fs::path& to) {
    std::error_code ec;
    // copy_file копирует и права доступа к файлу; существующий файл не перезаписывается.
    if (!fs::copy_file(from, to, fs::copy_options::none, ec) || ec)
        throw WorkerError("io_error", "Не удалось создать резервную копию: " + ec.message());
}

bool sameFile(const fs::path& a, const fs::path& b) {
    std::error_code ec;
    if (!fs::exists(a, ec) || !fs::exists(b, ec)) return false;
    return fs::equivalent(a, b, ec);
}

void ensureFreeSpace(const fs::path& dir, std::uint64_t needed) {
    std::error_code ec;
    auto info = fs::space(dir, ec);
    if (ec) return;  // неизвестно — проверит сама запись
    if (info.available < needed)
        throw WorkerError("no_space", "Недостаточно места на диске в каталоге назначения",
                          json{{"available", info.available}, {"needed", needed}});
}

void ensureWritable(const fs::path& p) {
#ifdef _WIN32
    HANDLE h = CreateFileW(p.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        DWORD err = GetLastError();
        if (err == ERROR_SHARING_VIOLATION || err == ERROR_LOCK_VIOLATION)
            throw WorkerError("file_locked", "Файл открыт в другой программе: " + pathToUtf8(p));
        throw WorkerError("access_denied", "Нет прав на запись файла: " + pathToUtf8(p));
    }
    CloseHandle(h);
#else
    std::FILE* f = std::fopen(p.c_str(), "r+b");
    if (!f) throw WorkerError("access_denied", "Нет прав на запись файла: " + pathToUtf8(p));
    std::fclose(f);
#endif
}

}  // namespace pm
