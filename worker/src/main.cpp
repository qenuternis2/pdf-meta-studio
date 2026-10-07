// pdfmeta-worker: отдельный процесс, связывающий qpdf и Adobe XMP Core.
// Протокол — JSON Lines через stdin/stdout, см. docs/PROTOCOL.md.
#include "pdf_edit.hpp"
#include "protocol.hpp"
#include "xmp_model.hpp"

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
        SetInformationJobObject(job, JobObjectExtendedLimitInformation, &info, sizeof(info));
        AssignProcessToJobObject(job, GetCurrentProcess());
    }
#else
    rlimit rl{memLimit, memLimit};
    setrlimit(RLIMIT_AS, &rl);
    rlimit core{0, 0};
    setrlimit(RLIMIT_CORE, &core);
#endif
}

struct Queue {
    std::mutex m;
    std::condition_variable cv;
    std::deque<json> items;
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
        while (std::getline(std::cin, line)) {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (line.empty()) continue;
            json req;
            try {
                req = json::parse(line);
            } catch (const std::exception&) {
                channel.send(json{{"id", nullptr}, {"type", "error"}, {"code", "bad_request"},
                                  {"message", "Строка запроса не является JSON"}});
                continue;
            }
            if (req.value("cmd", "") == "cancel") {
                if (req.value("target", std::int64_t(-1)) == currentId.load()) cancel = true;
                continue;
            }
            std::lock_guard<std::mutex> lock(queue.m);
            queue.items.push_back(std::move(req));
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
            req = std::move(queue.items.front());
            queue.items.pop_front();
        }
        std::int64_t id = req.value("id", std::int64_t(0));
        if (req.value("cmd", "") == "shutdown") {
            channel.send(json{{"id", id}, {"type", "result"}, {"data", json::object()}});
            break;
        }
        cancel = false;
        currentId = id;
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
