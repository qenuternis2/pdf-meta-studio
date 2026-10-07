// A deliberately unresponsive IPC peer for deadline and cancellation acceptance tests.
#include <iostream>
#include <string>
int main() {
    std::cout << "{\"type\":\"ready\",\"protocol\":1}" << std::endl;
    std::string line;
    while (std::getline(std::cin, line)) { }
    return 0;
}
