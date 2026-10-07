// A deliberately unresponsive IPC peer for deadline and cancellation acceptance tests.
#include <iostream>
#include <cstdio>
#include <string>
int main() {
    std::cout << "{\"type\":\"ready\",\"protocol\":1}" << std::endl;
    std::string line;
    while (std::getline(std::cin, line)) {
        if (line.find("malformed") != std::string::npos) std::cout << "invalid JSON" << std::endl;
        if (line.find("close-output") != std::string::npos) std::fclose(stdout);
    }
    return 0;
}
