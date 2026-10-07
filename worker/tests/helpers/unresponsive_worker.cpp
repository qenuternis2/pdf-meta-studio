// A deliberately unresponsive IPC peer for deadline and cancellation acceptance tests.
#include <iostream>
#include <cstdio>
#include <string>
#include <chrono>
#include <thread>

int main(int argc, char** argv) {
    if (argc == 4 && std::string(argv[1]) == "--format" && std::string(argv[2]) == "xml") {
        // Emulate a validator that exceeds its output limit and then remains alive.
        std::ostream& output = std::string(argv[3]).find("overflow-stderr") != std::string::npos
            ? std::cerr : std::cout;
        const std::string chunk(8192, 'x');
        for (int index = 0; index < 1025; ++index) output.write(chunk.data(), chunk.size());
        output.flush();
        std::this_thread::sleep_for(std::chrono::minutes(10));
        return 0;
    }
    std::cout << "{\"type\":\"ready\",\"protocol\":1}" << std::endl;
    std::string line;
    while (std::getline(std::cin, line)) {
        if (line.find("malformed") != std::string::npos) std::cout << "invalid JSON" << std::endl;
        if (line.find("close-output") != std::string::npos) std::fclose(stdout);
    }
    return 0;
}
