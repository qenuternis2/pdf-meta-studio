// pdfmeta-worker: отдельный процесс, связывающий qpdf и Adobe XMP Core.
// Протокол — JSON Lines через stdin/stdout, см. docs/PROTOCOL.md.
#include "pdf_edit.hpp"
#include "protocol.hpp"
#include "xmp_model.hpp"
#include "private_data.hpp"

#include <qpdf/QPDF.hh>

#include <condition_variable>
#include <cstdio>
#include <deque>
#include <iostream>
#include <thread>

#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#include <windows.h>
#else
#include <sys/resource.h>
#endif

using namespace pm;

namespace {

// Лимиты ресурсов процесса. Отдельный процесс сам по себе не песочница,
// но ограничение памяти не даёт испорченному PDF исчерпать систему.
void applyResourceLimits() {
    const unsigned long long memLimit = 4ull << 30;  // 4 ГиБ
#ifdef _WIN32
    HANDLE job = CreateJobObjectW(nullptr, nullptr);
    if (job) {
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION info{};
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_PROCESS_MEMORY | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;
        info.ProcessMemoryLimit = static_cast<SIZE_T>(memLimit);
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &info, sizeof(info)) ||
            !AssignProcessToJobObject(job, GetCurrentProcess())) {
            CloseHandle(job);
            throw WorkerError("resource_limits_unavailable", "Не удалось установить обязательный лимит памяти обработчика");
        }
    } else throw WorkerError("resource_limits_unavailable", "Не удалось создать ограниченный процесс обработки PDF");
#else
    rlimit rl{memLimit, memLimit};
    rlimit current{};
    if (getrlimit(RLIMIT_AS, &current) != 0) throw WorkerError("resource_limits_unavailable", "Не удалось прочитать ограничения памяти");
    if (current.rlim_max != RLIM_INFINITY && current.rlim_max < rl.rlim_max) rl.rlim_cur = rl.rlim_max = current.rlim_max;
    if (setrlimit(RLIMIT_AS, &rl) != 0) throw WorkerError("resource_limits_unavailable", "Не удалось ограничить память обработчика");
    rlimit core{0, 0};
    setrlimit(RLIMIT_CORE, &core);
#endif
}

struct Queue {
    std::mutex m;
    std::condition_variable cv;
    std::deque<std::pair<json, size_t>> items;
    size_t bytes = 0;
    bool closed = false;
};

json handle(const json& req, Context& ctx) {
    std::string cmd = req.value("cmd", "");
    if (cmd == "hello")
        return json{{"worker", PDFMETA_VERSION}, {"qpdf", QPDF::QPDFVersion()}, {"xmp", xmpToolkitVersion()},
                    {"protocol", 1}};
    if (cmd == "open") return openDocument(req, ctx);
    if (cmd == "validateXmp") {
        auto doc = XmpDoc::parse(req.at("xml").get<std::string>());
        doc->apply(json::array());
        return json{{"model", doc->model()}, {"packet", doc->serialize()}};
    }
    if (cmd == "preview") return previewEdits(req, ctx);
    if (cmd == "exportMetadata") return exportMetadata(req, ctx);
    if (cmd == "exportPrivate") return exportPrivate(req, ctx);
    if (cmd == "save") return saveEdits(req, ctx);
    throw WorkerError("bad_request", "Неизвестная команда: " + cmd);
}

}  // namespace

int main(int argc, char** argv) {
#ifdef _WIN32
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
#endif
    bool noLimits = argc > 1 && std::string(argv[1]) == "--no-limits";
    if (!noLimits) applyResourceLimits();

    xmpInitialize();
    Channel channel;
    std::atomic<bool> cancel{false};
    std::atomic<std::int64_t> currentId{-1};
    Queue queue;

    // Поток чтения stdin: команда cancel обрабатывается сразу, остальные — по очереди.
    std::thread reader([&] {
        std::string line;
        constexpr size_t maxLine = 128ull << 20;
        auto readLine = [&] {
            line.clear();
            bool overflow = false;
            auto* input = std::cin.rdbuf();
            for (;;) {
                auto c = input->sbumpc();
                if (c == std::char_traits<char>::eof()) return !line.empty() || overflow;
                if (c == '\n') break;
                if (line.size() < maxLine) line.push_back(static_cast<char>(c));
                else overflow = true;
            }
            if (overflow) {
                channel.send(json{{"id", nullptr}, {"type", "error"}, {"code", "request_too_large"}, {"message", "Запрос превышает 128 МиБ"}});
                line.clear();
            }
            return true;
        };
        while (readLine()) {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (line.empty()) continue;
            json req;
            try {
                req = json::parse(line, [](int depth, json::parse_event_t, json&) { if (depth > 128) throw std::invalid_argument("request nesting limit"); return true; });
                if (!req.is_object() || !req.contains("cmd") || !req["cmd"].is_string() ||
                    (req.contains("id") && !req["id"].is_number_integer()) ||
                    (req["cmd"] == "cancel" && req.contains("target") && !req["target"].is_number_integer()))
                    throw std::invalid_argument("invalid envelope");
            } catch (const std::exception&) {
                channel.send(json{{"id", nullptr}, {"type", "error"}, {"code", "bad_request"},
                                  {"message", "Строка запроса не является JSON"}});
                continue;
            }
            if (req.value("cmd", "") == "cancel") {
                auto target = req.value("target", std::int64_t(-1));
                std::lock_guard<std::mutex> lock(queue.m);
                if (target == currentId.load()) cancel = true;
                else for (auto it = queue.items.begin(); it != queue.items.end(); ++it) {
                    if (it->first.value("id", std::int64_t(0)) != target) continue;
                    queue.bytes -= it->second;
                    queue.items.erase(it);
                    channel.send(json{{"id", target}, {"type", "error"}, {"code", "cancelled"}, {"message", "Операция отменена до начала обработки"}});
                    break;
                }
                continue;
            }
            std::lock_guard<std::mutex> lock(queue.m);
            if (queue.items.size() >= 8 || line.size() > maxLine - queue.bytes) {
                channel.send(json{{"id", req.value("id", std::int64_t(0))}, {"type", "error"}, {"code", "queue_full"},
                                  {"message", "Очередь обработки заполнена; повторите запрос после завершения текущей операции"}});
                continue;
            }
            queue.bytes += line.size();
            queue.items.emplace_back(std::move(req), line.size());
            queue.cv.notify_one();
        }
        std::lock_guard<std::mutex> lock(queue.m);
        queue.closed = true;
        queue.cv.notify_one();
    });

    channel.send(json{{"type", "ready"}, {"protocol", 1}});
    while (true) {
        json req;
        {
            std::unique_lock<std::mutex> lock(queue.m);
            queue.cv.wait(lock, [&] { return queue.closed || !queue.items.empty(); });
            if (queue.items.empty()) break;
            req = std::move(queue.items.front().first);
            queue.bytes -= queue.items.front().second;
            queue.items.pop_front();
            cancel = false;
            currentId = req.value("id", std::int64_t(0));
        }
        std::int64_t id = req.value("id", std::int64_t(0));
        if (req.value("cmd", "") == "shutdown") {
            channel.send(json{{"id", id}, {"type", "result"}, {"data", json::object()}});
            break;
        }
        Context ctx(id, channel, cancel);
        try {
            json data = handle(req, ctx);
            channel.send(json{{"id", id}, {"type", "result"}, {"data", data}});
        } catch (const Cancelled&) {
            channel.send(json{{"id", id}, {"type", "error"}, {"code", "cancelled"}, {"message", "Операция отменена"}});
        } catch (const WorkerError& e) {
            channel.send(json{{"id", id}, {"type", "error"}, {"code", e.code}, {"message", sanitizeUtf8(e.what())},
                              {"details", e.details}});
        } catch (const std::bad_alloc&) {
            channel.send(json{{"id", id}, {"type", "error"}, {"code", "out_of_memory"},
                              {"message", "Недостаточно памяти для обработки документа"}});
        } catch (const std::exception& e) {
            channel.send(json{{"id", id}, {"type", "error"}, {"code", "internal"}, {"message", sanitizeUtf8(e.what())}});
        }
        currentId = -1;
    }
    xmpTerminate();
    // Поток чтения может ждать stdin; процесс завершается без join.
    reader.detach();
    std::fflush(stdout);
    std::_Exit(0);
}
