#include "fileutil.hpp"

#include "sha256.hpp"

#include <cerrno>
#include <chrono>
#include <cstdio>
#include <fstream>
#include <iterator>
#include <memory>
#include <random>
#include <vector>

#ifdef _WIN32
#include <windows.h>
#include <aclapi.h>
#include <sddl.h>
#include <fcntl.h>
#include <io.h>
#else
#include <fcntl.h>
#include <unistd.h>
#include <sys/stat.h>
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
    return json{{"size", size}, {"mtime", mtime}, {"sha256", sha256}, {"identity", identity}};
}

Fingerprint Fingerprint::fromJson(const json& j) {
    Fingerprint f;
    f.size = j.at("size").get<std::uint64_t>();
    f.mtime = j.at("mtime").get<std::string>();
    f.sha256 = j.at("sha256").get<std::string>();
    f.identity = j.value("identity", "");
    return f;
}

Fingerprint computeFingerprint(const fs::path& p, Context* ctx) {
    Fingerprint f;
#ifdef _WIN32
    HANDLE file = CreateFileW(p.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) throw WorkerError("file_locked", "Не удалось определить идентичность файла");
    BY_HANDLE_FILE_INFORMATION info{};
    bool identified = GetFileInformationByHandle(file, &info);
    CloseHandle(file);
    if (!identified) throw WorkerError("io_error", "Файловая система не возвращает идентичность файла");
    f.identity = std::to_string(info.dwVolumeSerialNumber) + ":" + std::to_string(info.nFileIndexHigh) + ":" + std::to_string(info.nFileIndexLow);
#else
    struct stat info{};
    if (::stat(p.c_str(), &info) != 0) throw WorkerError("file_not_found", "Не удалось определить идентичность файла");
    f.identity = std::to_string(info.st_dev) + ":" + std::to_string(info.st_ino);
#endif
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
    if (in.bad() || done != f.size) throw WorkerError("io_error", "Не удалось полностью прочитать исходный файл");
    return f;
}

#ifdef _WIN32
static void copyAcl(const fs::path& source, const fs::path& destination) {
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    PACL dacl = nullptr;
    DWORD error = GetNamedSecurityInfoW(source.c_str(), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION,
                                         nullptr, nullptr, &dacl, nullptr, &descriptor);
    if (error != ERROR_SUCCESS) throw WorkerError("access_denied", "Не удалось прочитать ограничения доступа исходного файла");
    SECURITY_DESCRIPTOR_CONTROL control = 0;
    DWORD revision = 0;
    GetSecurityDescriptorControl(descriptor, &control, &revision);
    error = SetNamedSecurityInfoW(const_cast<wchar_t*>(destination.c_str()), SE_FILE_OBJECT,
        DACL_SECURITY_INFORMATION | ((control & SE_DACL_PROTECTED) ? PROTECTED_DACL_SECURITY_INFORMATION : UNPROTECTED_DACL_SECURITY_INFORMATION),
        nullptr, nullptr, dacl, nullptr);
    LocalFree(descriptor);
    if (error != ERROR_SUCCESS) throw WorkerError("access_denied", "Не удалось перенести ограничения доступа на новый файл");
}
#endif

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

TemporaryFile::TemporaryFile(const fs::path& dir) {
#ifdef _WIN32
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
        throw WorkerError("access_denied", "Не удалось определить владельца временного файла");
    std::unique_ptr<void, decltype(&CloseHandle)> tokenGuard(token, &CloseHandle);
    DWORD length = 0;
    GetTokenInformation(token, TokenUser, nullptr, 0, &length);
    std::vector<unsigned char> user(length);
    bool foundUser = length && GetTokenInformation(token, TokenUser, user.data(), length, &length);
    tokenGuard.reset();
    if (!foundUser) throw WorkerError("access_denied", "Не удалось определить владельца временного файла");
    LPWSTR rawSid = nullptr;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(user.data())->User.Sid, &rawSid))
        throw WorkerError("access_denied", "Не удалось защитить временный файл");
    std::unique_ptr<void, decltype(&LocalFree)> sid(rawSid, &LocalFree);
    std::wstring sddl = L"D:P(A;;FA;;;" + std::wstring(rawSid) + L")";
    PSECURITY_DESCRIPTOR rawDescriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &rawDescriptor, nullptr))
        throw WorkerError("access_denied", "Не удалось защитить временный файл");
    std::unique_ptr<void, decltype(&LocalFree)> descriptor(rawDescriptor, &LocalFree);
    SECURITY_ATTRIBUTES attributes{sizeof(SECURITY_ATTRIBUTES), descriptor.get(), FALSE};
#endif
    for (int i = 0; i < 100; ++i) {
        fs::path candidate = dir / pathFromUtf8(".pdfmeta-" + randomToken() + ".tmp");
#ifdef _WIN32
        // Verification uses this same stream; no other reader, writer or deletion is needed.
        HANDLE h = CreateFileW(candidate.c_str(), GENERIC_READ | GENERIC_WRITE, 0,
                               &attributes, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h != INVALID_HANDLE_VALUE) {
            int fd = _open_osfhandle(reinterpret_cast<intptr_t>(h), _O_BINARY | _O_RDWR | _O_NOINHERIT);
            if (fd < 0) CloseHandle(h);
            else {
                stream_ = _fdopen(fd, "r+b");
                if (!stream_) _close(fd);
            }
            if (stream_) { path = candidate; return; }
            std::error_code ignored; fs::remove(candidate, ignored);
            break;
        }
        if (GetLastError() != ERROR_FILE_EXISTS) break;
#else
        int fd = ::open(candidate.c_str(), O_RDWR | O_CREAT | O_EXCL | O_CLOEXEC, 0600);
        if (fd >= 0) {
            stream_ = ::fdopen(fd, "r+b");
            if (stream_) { path = candidate; return; }
            ::close(fd);
            std::error_code ignored; fs::remove(candidate, ignored);
            break;
        }
        if (errno != EEXIST) break;
#endif
    }
    throw WorkerError("io_error", "Не удалось создать временный файл в каталоге назначения");
}

TemporaryFile::~TemporaryFile() {
    if (stream_) std::fclose(stream_);
    if (!keep && !path.empty()) { std::error_code ignored; fs::remove(path, ignored); }
}

void TemporaryFile::write(const std::string& bytes) {
    if (std::fwrite(bytes.data(), 1, bytes.size(), stream_) != bytes.size())
        throw WorkerError("write_failed", "Не удалось записать временный файл");
}

void TemporaryFile::flush() {
    if (std::fflush(stream_) != 0 || std::ferror(stream_))
        throw WorkerError("write_failed", "Не удалось записать временный файл");
}

void TemporaryFile::close() {
    if (!stream_) return;
    auto file = stream_; stream_ = nullptr;
    if (std::fclose(file) != 0) throw WorkerError("write_failed", "Не удалось завершить запись временного файла");
}

fs::path tempPathIn(const fs::path& dir) {
    TemporaryFile temporary(dir);
    temporary.close();
    temporary.keep = true;
    return temporary.path;
}

void carryOverProtection(const fs::path& source, const fs::path& target, const fs::path& temp) {
#ifdef _WIN32
    std::error_code ec;
    // Копия или замена файла из Интернета должна остаться помеченной, иначе программы просмотра
    // перестанут открывать её в защищённом режиме.
    std::ifstream in(fs::path(source.native() + L":Zone.Identifier"), std::ios::binary);
    if (in) {
        std::string zone((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
        std::ofstream out(fs::path(temp.native() + L":Zone.Identifier"), std::ios::binary | std::ios::trunc);
        out.write(zone.data(), static_cast<std::streamsize>(zone.size()));
        if (!out) throw WorkerError("io_error", "Не удалось перенести отметку «загружено из Интернета» на новый файл");
    }
    // Keep the private pending ACL until alternate-stream writes finish, including read-only sources.
    copyAcl(fs::exists(target, ec) ? target : source, temp);
#else
    std::error_code ec;
    fs::perms p = fs::status(fs::exists(target, ec) ? target : source, ec).permissions();
    if (ec) throw WorkerError("io_error", "Не удалось прочитать права доступа файла: " + ec.message());
    fs::permissions(temp, p & fs::perms::all, ec);
    if (ec) throw WorkerError("io_error", "Не удалось установить права доступа файла: " + ec.message());
#endif
}

void replaceFile(const fs::path& from, const fs::path& to) {
#ifdef _WIN32
    auto fail = [](DWORD err) {
        if (err == ERROR_SHARING_VIOLATION || err == ERROR_LOCK_VIOLATION || err == ERROR_ACCESS_DENIED ||
            err == ERROR_UNABLE_TO_REMOVE_REPLACED)
            throw WorkerError("file_locked", "Файл назначения занят другой программой или защищён от записи");
        throw WorkerError("io_error", "Не удалось переименовать временный файл (код " + std::to_string(err) + ")");
    };
    std::error_code ec;
    if (fs::exists(to, ec)) {
        if (ReplaceFileW(to.c_str(), from.c_str(), nullptr, REPLACEFILE_WRITE_THROUGH | REPLACEFILE_IGNORE_MERGE_ERRORS,
                         nullptr, nullptr))
            return;
        DWORD err = GetLastError();
        // Файловые системы без поддержки ReplaceFileW: обычное переименование.
        if (err != ERROR_INVALID_FUNCTION && err != ERROR_NOT_SUPPORTED && err != ERROR_INVALID_PARAMETER) fail(err);
    }
    if (!MoveFileExW(from.c_str(), to.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) fail(GetLastError());
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
#ifdef _WIN32
    copyAcl(from, to);
#endif
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
