#pragma once

#include "protocol.hpp"

#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <string>

namespace pm {

namespace fs = std::filesystem;

fs::path pathFromUtf8(const std::string& s);
std::string pathToUtf8(const fs::path& p);

// Отпечаток файла для обнаружения внешнего изменения: размер, время изменения, SHA-256.
struct Fingerprint {
    std::uint64_t size = 0;
    std::string mtime;  // число тиков file_time_type в виде строки
    std::string sha256;
    std::string identity;
    json toJson() const;
    static Fingerprint fromJson(const json& j);
    bool operator==(const Fingerprint& o) const {
        return size == o.size && mtime == o.mtime && sha256 == o.sha256 && identity == o.identity;
    }
};

Fingerprint computeFingerprint(const fs::path& p, Context* ctx = nullptr);

// Уникальное имя рядом с файлом: <stem><suffix>.pdf, <stem><suffix> (2).pdf ...
fs::path uniqueSibling(const fs::path& dir, const std::string& stemUtf8, const std::string& ext);
// Создаёт пустой временный файл в каталоге монопольно (не через существующий файл или ссылку).
fs::path tempPathIn(const fs::path& dir);

// Keep the exclusively created file open: writers must not reopen its replaceable pathname.
class TemporaryFile {
public:
    explicit TemporaryFile(const fs::path& dir);
    ~TemporaryFile();
    TemporaryFile(const TemporaryFile&) = delete;
    TemporaryFile& operator=(const TemporaryFile&) = delete;
    std::FILE* stream() const { return stream_; }
    void write(const std::string& bytes);
    void flush();
    void close();
    fs::path path;
    bool keep = false;
private:
    std::FILE* stream_ = nullptr;
};

// Перенос защиты на новый файл до его переименования в target:
// POSIX — права доступа файла назначения (если он есть) или исходного;
// Windows — метка «загружено из Интернета» (поток Zone.Identifier) исходного файла.
void carryOverProtection(const fs::path& source, const fs::path& target, const fs::path& temp);
// Атомарная замена (rename поверх существующего файла в том же каталоге).
// В Windows существующий файл заменяется через ReplaceFileW: его ACL, атрибуты
// и именованные потоки сохраняются.
void replaceFile(const fs::path& from, const fs::path& to);
void copyFileExact(const fs::path& from, const fs::path& to);
bool sameFile(const fs::path& a, const fs::path& b);
void ensureFreeSpace(const fs::path& dir, std::uint64_t needed);
// Проверка, что файл можно открыть на запись (не занят другим процессом).
void ensureWritable(const fs::path& p);

}  // namespace pm
