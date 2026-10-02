#include "protocol.hpp"

#include <cstdio>

namespace pm {

void Channel::send(const json& message) {
    std::string line = message.dump(-1, ' ', false, json::error_handler_t::replace);
    line.push_back('\n');
    std::lock_guard<std::mutex> lock(mutex_);
    std::fwrite(line.data(), 1, line.size(), stdout);
    std::fflush(stdout);
}

void Context::progress(const std::string& stage, int percent) {
    if (percent == lastPercent_ && stage == lastStage_) return;
    lastPercent_ = percent;
    lastStage_ = stage;
    channel_.send(json{{"id", id_}, {"type", "progress"}, {"stage", stage}, {"percent", percent}});
}

static int utf8SeqLen(unsigned char c) {
    if (c < 0x80) return 1;
    if ((c >> 5) == 0x6) return 2;
    if ((c >> 4) == 0xE) return 3;
    if ((c >> 3) == 0x1E) return 4;
    return 0;
}

static bool validSeq(const std::string& s, size_t i, int len) {
    if (i + len > s.size()) return false;
    unsigned char c0 = static_cast<unsigned char>(s[i]);
    unsigned cp = len == 2 ? (c0 & 0x1F) : len == 3 ? (c0 & 0x0F) : (c0 & 0x07);
    for (int k = 1; k < len; ++k) {
        unsigned char c = static_cast<unsigned char>(s[i + k]);
        if ((c >> 6) != 0x2) return false;
        cp = (cp << 6) | (c & 0x3F);
    }
    if (len == 2 && cp < 0x80) return false;
    if (len == 3 && (cp < 0x800 || (cp >= 0xD800 && cp <= 0xDFFF))) return false;
    if (len == 4 && (cp < 0x10000 || cp > 0x10FFFF)) return false;
    return true;
}

bool isValidUtf8(const std::string& s) {
    for (size_t i = 0; i < s.size();) {
        int len = utf8SeqLen(static_cast<unsigned char>(s[i]));
        if (len == 0) return false;
        if (len > 1 && !validSeq(s, i, len)) return false;
        i += len;
    }
    return true;
}

std::string sanitizeUtf8(const std::string& s) {
    if (isValidUtf8(s)) return s;
    std::string out;
    out.reserve(s.size());
    for (size_t i = 0; i < s.size();) {
        int len = utf8SeqLen(static_cast<unsigned char>(s[i]));
        if (len == 1 || (len > 1 && validSeq(s, i, len))) {
            out.append(s, i, len);
            i += len;
        } else {
            out += "\xEF\xBF\xBD";
            ++i;
        }
    }
    return out;
}

static const char* kB64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

std::string toBase64(const std::string& in) {
    std::string out;
    out.reserve((in.size() + 2) / 3 * 4);
    size_t i = 0;
    while (i + 2 < in.size()) {
        unsigned v = (static_cast<unsigned char>(in[i]) << 16) |
                     (static_cast<unsigned char>(in[i + 1]) << 8) | static_cast<unsigned char>(in[i + 2]);
        out += kB64[(v >> 18) & 63];
        out += kB64[(v >> 12) & 63];
        out += kB64[(v >> 6) & 63];
        out += kB64[v & 63];
        i += 3;
    }
    if (i < in.size()) {
        unsigned v = static_cast<unsigned char>(in[i]) << 16;
        if (i + 1 < in.size()) v |= static_cast<unsigned char>(in[i + 1]) << 8;
        out += kB64[(v >> 18) & 63];
        out += kB64[(v >> 12) & 63];
        out += (i + 1 < in.size()) ? kB64[(v >> 6) & 63] : '=';
        out += '=';
    }
    return out;
}

std::string fromBase64(const std::string& in) {
    std::string out;
    unsigned buf = 0;
    int bits = 0;
    for (char ch : in) {
        if (ch == '=' || ch == '\n' || ch == '\r' || ch == ' ') continue;
        const char* p = std::char_traits<char>::find(kB64, 64, ch);
        if (!p) throw WorkerError("bad_request", "Некорректные данные base64");
        buf = (buf << 6) | static_cast<unsigned>(p - kB64);
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            out += static_cast<char>((buf >> bits) & 0xFF);
        }
    }
    return out;
}

std::string toHex(const std::string& bytes) {
    static const char* h = "0123456789abcdef";
    std::string out;
    out.reserve(bytes.size() * 2);
    for (unsigned char c : bytes) {
        out += h[c >> 4];
        out += h[c & 15];
    }
    return out;
}

}  // namespace pm
