// Synthetic data only: check the production temporary-file primitive before PDF serialization.
#include "fileutil.hpp"
#include <fstream>
#include <iostream>
#include <iterator>
#include <memory>
#include <stdexcept>
#include <vector>
#ifdef _WIN32
#include <windows.h>
#include <aclapi.h>
#include <sddl.h>
#else
#include <sys/stat.h>
#endif

namespace {
void require(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}
std::string read(const pm::fs::path& path) {
    std::ifstream file(path, std::ios::binary);
    return {std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>()};
}
#ifdef _WIN32
void allowEveryoneInTestDirectory(const pm::fs::path& directory) {
    PSECURITY_DESCRIPTOR raw = nullptr;
    require(ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;OICI;FA;;;WD)", 1, &raw, nullptr),
            "Could not configure the synthetic shared directory");
    std::unique_ptr<void, decltype(&LocalFree)> descriptor(raw, &LocalFree);
    PACL acl = nullptr; BOOL present = FALSE, defaulted = FALSE;
    require(GetSecurityDescriptorDacl(raw, &present, &acl, &defaulted) && present && acl,
            "Test directory DACL missing");
    require(SetNamedSecurityInfoW(const_cast<wchar_t*>(directory.c_str()), SE_FILE_OBJECT,
        DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION, nullptr, nullptr, acl, nullptr) == ERROR_SUCCESS,
        "Could not set the synthetic shared directory DACL");
}
void requireCreatorOnly(const pm::fs::path& path) {
    PSECURITY_DESCRIPTOR raw = nullptr; PACL acl = nullptr;
    require(GetNamedSecurityInfoW(path.c_str(), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION,
        nullptr, nullptr, &acl, nullptr, &raw) == ERROR_SUCCESS, "Cannot read temporary DACL");
    std::unique_ptr<void, decltype(&LocalFree)> descriptor(raw, &LocalFree);
    SECURITY_DESCRIPTOR_CONTROL control = 0; DWORD revision = 0;
    require(GetSecurityDescriptorControl(raw, &control, &revision) && (control & SE_DACL_PROTECTED),
            "Temporary file inherits its shared directory permissions");
    require(acl && acl->AceCount == 1, "Temporary file grants access beyond its creator");
    void* rawAce = nullptr;
    require(GetAce(acl, 0, &rawAce), "Cannot read temporary ACE");
    auto ace = static_cast<ACCESS_ALLOWED_ACE*>(rawAce);
    require(ace->Header.AceType == ACCESS_ALLOWED_ACE_TYPE && !(ace->Header.AceFlags & INHERITED_ACE),
            "Temporary ACE is not an explicit allow entry");
    HANDLE token = nullptr;
    require(OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token), "Cannot query creator token");
    std::unique_ptr<void, decltype(&CloseHandle)> tokenGuard(token, &CloseHandle);
    DWORD length = 0; GetTokenInformation(token, TokenUser, nullptr, 0, &length);
    std::vector<unsigned char> user(length);
    require(length && GetTokenInformation(token, TokenUser, user.data(), length, &length), "Cannot query creator SID");
    require(EqualSid(&ace->SidStart, reinterpret_cast<TOKEN_USER*>(user.data())->User.Sid),
            "Temporary DACL allows a principal other than the creator");
    require((ace->Mask & FILE_ALL_ACCESS) == FILE_ALL_ACCESS, "Creator cannot use the temporary file");
}
#endif
}

int main(int argc, char** argv) {
    try {
        require(argc == 2, "Pass an isolated fixture directory");
        const auto root = pm::pathFromUtf8(argv[1]) / "secure-temporary-file";
        require(pm::fs::create_directory(root), "Fixture directory already exists");
        const auto victim = root / "unrelated.txt";
        { std::ofstream file(victim); file << "unrelated sentinel"; }
#ifdef _WIN32
        allowEveryoneInTestDirectory(root);
#endif
        pm::fs::path temporaryPath;
        {
            pm::TemporaryFile temporary(root);
            temporaryPath = temporary.path;
#ifdef _WIN32
            requireCreatorOnly(temporary.path); // Before writing any document bytes.
            const auto moved = root / "attacker-moved.tmp";
            require(!MoveFileExW(temporary.path.c_str(), moved.c_str(), 0), "Temporary file can be renamed during writing");
            require(GetLastError() == ERROR_SHARING_VIOLATION, "Unexpected rename refusal");
            HANDLE writer = CreateFileW(temporary.path.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                         nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (writer != INVALID_HANDLE_VALUE) CloseHandle(writer);
            require(writer == INVALID_HANDLE_VALUE, "A second writer can modify the temporary file");
            temporary.write("private synthetic metadata");
            temporary.flush();
            require(read(temporary.path) == "private synthetic metadata", "Verification reader cannot read pending output");
#else
            struct stat state{};
            require(::stat(temporary.path.c_str(), &state) == 0 && (state.st_mode & 0777) == 0600,
                    "Temporary permissions expose data to other accounts");
            // Deterministically model replacement of a pathname in a writable shared directory.
            const auto moved = root / "attacker-moved.tmp";
            pm::fs::rename(temporary.path, moved);
            pm::fs::create_symlink(victim, temporary.path);
            temporary.write("private synthetic metadata");
            temporary.flush();
            require(read(victim) == "unrelated sentinel", "Writer followed the substituted symlink");
            require(read(moved) == "private synthetic metadata", "Writer lost its exclusively created descriptor");
            pm::fs::remove(temporary.path);
            pm::fs::rename(moved, temporary.path);
#endif
            temporary.close();
        }
        require(!pm::fs::exists(temporaryPath), "Pending temporary file was not cleaned up");
        require(read(victim) == "unrelated sentinel", "Unrelated fixture was overwritten");
        pm::fs::remove(victim);
        pm::fs::remove(root);
        std::cout << "PASS private temporary ACL/mode, retained descriptor, write/verify and cleanup\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
