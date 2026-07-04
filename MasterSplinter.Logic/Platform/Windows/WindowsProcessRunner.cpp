// WindowsProcessRunner.cpp — Windows Adapter implementation.
//
// This is the Windows-only seam, so it deliberately mixes **classic Win32 + modern APIs** for the
// most capable, safest implementation:
//   * Win32     — CreateProcessW is the spawn primitive (WinRT has no "spawn + capture stdout" API),
//                 plus CommandLineToArgvW-compatible argument quoting.
//   * C++/WinRT — winrt::to_hstring for UTF-8 -> UTF-16 (replaces a hand-rolled MultiByteToWideChar).
//   * WIL       — wil::unique_handle / unique_hfile / scope_exit for RAII (no manual CloseHandle).
//   * modern Win32 — STARTUPINFOEX + PROC_THREAD_ATTRIBUTE_HANDLE_LIST so the child inherits ONLY the
//                 two handles it needs, not every inheritable handle in the process.
// Includes <windows.h> via framework.h (WIN32_LEAN_AND_MEAN). The portable core uses none of this.

#include "../../framework.h"
#include "WindowsProcessRunner.h"

#include <winrt/base.h>
#include <wil/resource.h>

#include <string>
#include <string_view>
#include <vector>

namespace ms
{
    namespace
    {
        // Quote one argument per the CommandLineToArgvW rules so paths with spaces/quotes survive.
        void AppendArg(std::wstring& cmd, std::wstring_view arg)
        {
            if (!cmd.empty())
                cmd += L' ';
            if (!arg.empty() && arg.find_first_of(L" \t\"") == std::wstring_view::npos)
            {
                cmd.append(arg);
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
    // Returns false only if the process could not be started; otherwise exitCode holds the child's
    // status. The executable is resolved via PATH (lpApplicationName = nullptr).
    bool WindowsProcessRunner::Run(const std::string& executable,
                                   const std::vector<std::string>& args,
                                   std::string& out,
                                   int& exitCode) const
    {
        out.clear();
        exitCode = -1;

        try
        {
            SECURITY_ATTRIBUTES sa{};
            sa.nLength = sizeof(sa);
            sa.bInheritHandle = TRUE; // the write end + NUL stdin must be inheritable by the child

            // Pipe: the child writes stdout+stderr into writePipe; we read from readPipe.
            wil::unique_handle readPipe, writePipe;
            if (!CreatePipe(readPipe.put(), writePipe.put(), &sa, 0))
                return false;
            SetHandleInformation(readPipe.get(), HANDLE_FLAG_INHERIT, 0); // our read end stays private

            // Give the child a NUL stdin so it can never block waiting on input (git reads none here).
            wil::unique_hfile nulIn(CreateFileW(L"NUL", GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE, &sa, OPEN_EXISTING, 0, nullptr));
            if (!nulIn)
                return false;

            // Command line: UTF-8 -> UTF-16 via C++/WinRT, then Win32-quoted.
            std::wstring cmd;
            AppendArg(cmd, winrt::to_hstring(executable));
            for (const auto& a : args)
                AppendArg(cmd, winrt::to_hstring(a));
            std::vector<wchar_t> cmdBuf(cmd.begin(), cmd.end());
            cmdBuf.push_back(L'\0'); // CreateProcessW needs a writable buffer

            // Restrict inheritance to exactly these two handles.
            HANDLE inheritList[2] = { writePipe.get(), nulIn.get() };

            STARTUPINFOEXW six{};
            six.StartupInfo.cb = sizeof(six);
            six.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            six.StartupInfo.hStdOutput = writePipe.get();
            six.StartupInfo.hStdError = writePipe.get();
            six.StartupInfo.hStdInput = nulIn.get();

            SIZE_T attrSize = 0;
            InitializeProcThreadAttributeList(nullptr, 1, 0, &attrSize); // query required size
            std::vector<unsigned char> attrBuf(attrSize);
            six.lpAttributeList = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attrBuf.data());
            if (!InitializeProcThreadAttributeList(six.lpAttributeList, 1, 0, &attrSize))
                return false;
            auto cleanupAttr = wil::scope_exit([&] { DeleteProcThreadAttributeList(six.lpAttributeList); });
            if (!UpdateProcThreadAttribute(six.lpAttributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                                           inheritList, sizeof(inheritList), nullptr, nullptr))
                return false;

            PROCESS_INFORMATION rawPi{};
            const BOOL ok = CreateProcessW(
                nullptr, cmdBuf.data(), nullptr, nullptr, TRUE,
                CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT,
                nullptr, nullptr, &six.StartupInfo, &rawPi);
            if (!ok)
                return false;
            wil::unique_handle childProcess(rawPi.hProcess);
            wil::unique_handle childThread(rawPi.hThread);

            writePipe.reset(); // drop our write end so ReadFile sees EOF when the child exits

            char buf[4096];
            DWORD read = 0;
            while (ReadFile(readPipe.get(), buf, sizeof(buf), &read, nullptr) && read > 0)
                out.append(buf, read);

            WaitForSingleObject(childProcess.get(), INFINITE);
            DWORD code = 0;
            if (GetExitCodeProcess(childProcess.get(), &code))
                exitCode = static_cast<int>(code);
            return true;
        }
        catch (...)
        {
            // e.g. winrt::to_hstring on malformed UTF-8 — treat as "could not start".
            return false;
        }
    }
}
