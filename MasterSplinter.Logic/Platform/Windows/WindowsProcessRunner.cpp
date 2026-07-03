// WindowsProcessRunner.cpp — Windows Adapter implementation (CreateProcessW + pipes).
//
// This is the one file in the core that is genuinely Windows-specific; it includes <windows.h>
// (via framework.h, which sets WIN32_LEAN_AND_MEAN). The launch/quoting/encoding logic here was
// moved verbatim from the original GitBackend.cpp.

#include "../../framework.h"
#include "WindowsProcessRunner.h"

#include <string>
#include <vector>

namespace ms
{
    namespace
    {
        std::wstring Widen(const std::string& utf8)
        {
            if (utf8.empty())
                return std::wstring();
            const int len = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
            std::wstring w(static_cast<size_t>(len), L'\0');
            MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), w.data(), len);
            return w;
        }

        // Quote one argument per the CommandLineToArgvW rules so paths with spaces/quotes survive.
        void AppendArg(std::wstring& cmd, const std::wstring& arg)
        {
            if (!cmd.empty())
                cmd += L' ';
            if (!arg.empty() && arg.find_first_of(L" \t\"") == std::wstring::npos)
            {
                cmd += arg;
                return;
            }
            cmd += L'"';
            for (size_t i = 0; i < arg.size();)
            {
                size_t backslashes = 0;
                while (i < arg.size() && arg[i] == L'\\') { ++backslashes; ++i; }
                if (i == arg.size())
                {
                    cmd.append(backslashes * 2, L'\\');
                    break;
                }
                if (arg[i] == L'"')
                {
                    cmd.append(backslashes * 2 + 1, L'\\');
                    cmd += L'"';
                }
                else
                {
                    cmd.append(backslashes, L'\\');
                    cmd += arg[i];
                }
                ++i;
            }
            cmd += L'"';
        }
    }

    // Launch `executable` with the given args, capturing stdout (and stderr, merged) as UTF-8.
    // Returns false only if the process could not be started; otherwise exitCode holds the
    // child's status. The executable is resolved via PATH (lpApplicationName = nullptr).
    bool WindowsProcessRunner::Run(const std::string& executable,
                                   const std::vector<std::string>& args,
                                   std::string& out,
                                   int& exitCode) const
    {
        out.clear();
        exitCode = -1;

        SECURITY_ATTRIBUTES sa{};
        sa.nLength = sizeof(sa);
        sa.bInheritHandle = TRUE;

        HANDLE readPipe = nullptr, writePipe = nullptr;
        if (!CreatePipe(&readPipe, &writePipe, &sa, 0))
            return false;
        SetHandleInformation(readPipe, HANDLE_FLAG_INHERIT, 0); // child must not inherit the read end

        std::wstring cmd;
        AppendArg(cmd, Widen(executable));
        for (const auto& a : args)
            AppendArg(cmd, Widen(a));

        STARTUPINFOW si{};
        si.cb = sizeof(si);
        si.dwFlags = STARTF_USESTDHANDLES;
        si.hStdOutput = writePipe;
        si.hStdError = writePipe;
        si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);

        PROCESS_INFORMATION pi{};
        std::vector<wchar_t> cmdBuf(cmd.begin(), cmd.end());
        cmdBuf.push_back(L'\0'); // CreateProcessW needs a writable buffer

        const BOOL ok = CreateProcessW(
            nullptr, cmdBuf.data(), nullptr, nullptr,
            TRUE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi);

        CloseHandle(writePipe); // drop our write end so ReadFile sees EOF at child exit

        if (!ok)
        {
            CloseHandle(readPipe);
            return false;
        }

        char buf[4096];
        DWORD read = 0;
        while (ReadFile(readPipe, buf, sizeof(buf), &read, nullptr) && read > 0)
            out.append(buf, read);
        CloseHandle(readPipe);

        WaitForSingleObject(pi.hProcess, INFINITE);
        DWORD code = 0;
        if (GetExitCodeProcess(pi.hProcess, &code))
            exitCode = static_cast<int>(code);
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
        return true;
    }
}
