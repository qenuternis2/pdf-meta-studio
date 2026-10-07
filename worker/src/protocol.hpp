// Протокол обмена с GUI: JSON Lines (UTF-8) через stdin/stdout.
// Каждая строка stdin — запрос {"id":N,"cmd":"..."}; ответы и прогресс идут в stdout.
// Пароли приходят только в теле запроса и никогда не пишутся в stderr/лог.
#pragma once

#include <nlohmann/json.hpp>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <mutex>
#include <stdexcept>
#include <string>

namespace pm {

using json = nlohmann::ordered_json;

// Ошибка, которую GUI показывает пользователю. code — машинный код (см. docs/PROTOCOL.md).
struct WorkerError : std::runtime_error {
    std::string code;
    json details;
    WorkerError(std::string code_, const std::string& message, json details_ = json::object())
        : std::runtime_error(message), code(std::move(code_)), details(std::move(details_)) {}
};

struct Cancelled : std::exception {
    const char* what() const noexcept override { return "cancelled"; }
};

class Channel {
public:
    void send(const json& message);
private:
    std::mutex mutex_;
};

// Контекст выполнения одной команды: прогресс и проверка отмены.
class Context {
public:
    Context(std::int64_t id, Channel& channel, std::atomic<bool>& cancel, int timeoutMs = 300000)
        : id_(id), channel_(channel), cancel_(cancel), deadline_(std::chrono::steady_clock::now() + std::chrono::milliseconds(timeoutMs)) {}
    void progress(const std::string& stage, int percent);
    void checkCancel() const {
        if (cancel_.load()) throw Cancelled();
        if (std::chrono::steady_clock::now() >= deadline_)
            throw WorkerError("operation_timeout", "Превышено время обработки; исходный файл не изменён");
    }
    bool cancelRequested() const { return cancel_.load(); }
    std::int64_t id() const { return id_; }
private:
    std::int64_t id_;
    Channel& channel_;
    std::atomic<bool>& cancel_;
    std::chrono::steady_clock::time_point deadline_;
    int lastPercent_ = -1;
    std::string lastStage_;
};

// Строка в JSON всегда должна быть корректным UTF-8: недопустимые байты заменяются U+FFFD.
std::string sanitizeUtf8(const std::string& s);
bool isValidUtf8(const std::string& s);
std::string toBase64(const std::string& bytes);
std::string fromBase64(const std::string& text);
std::string toHex(const std::string& bytes);

}  // namespace pm
