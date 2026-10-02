#pragma once

#include <cstdint>
#include <string>

namespace pm {

class Sha256 {
public:
    Sha256();
    void update(const void* data, size_t len);
    std::string hexDigest();  // завершает вычисление
    static std::string hex(const std::string& data);
private:
    void block(const unsigned char* p);
    std::uint32_t h_[8];
    unsigned char buf_[64];
    size_t bufLen_ = 0;
    std::uint64_t total_ = 0;
};

}  // namespace pm
