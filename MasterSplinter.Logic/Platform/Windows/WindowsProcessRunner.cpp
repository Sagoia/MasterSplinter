// WindowsProcessRunner.cpp — Windows Adapter implementation.
//
// This is the Windows-only seam, so it deliberately mixes **classic Win32 + modern APIs** for the
// most capable, safest implementation:
//   * Win32     — CreateProcessW is the spawn primitive (WinRT has no "spawn + capture stdout" API),
//                 plus CommandLineToArgvW-compatible argument quoting.
//   * C++/WinRT — winrt::to_hstring for UTF-8 -> UTF-16 (replaces a hand-rolled MultiByteToWideChar).
//   * WIL       — wil::unique_handle / unique_hfile / scope_exit for RAII (no manual CloseHandle).
//   * modern Win32 — STARTUPINFOEX + PROC_THREAD_ATTRIBUTE_HANDLE_LIST so the child inherits ONLY the
//                 two handles it needs, not every inheritable handle in the process;
//                 a job object so cancelling kills git's whole process tree;
//                 CompareStringOrdinal for the case-insensitive environment-block ordering.
// Includes <windows.h> via framework.h (WIN32_LEAN_AND_MEAN). The portable core uses none of this.

#include "../../framework.h"
#include "WindowsProcessRunner.h"

#include <winrt/base.h>
#include <wil/resource.h>

#include <atomic>
#include <chrono>
#include <cwchar>
#include <map>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

namespace ms
{
    namespace
    {
        // How often the sink is poked while the child produces no output. Small enough that
        // Cancel feels immediate, large enough to be free.
        constexpr auto kHeartbeatInterval = std::chrono::milliseconds(250);

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

        // Environment variable names are case-insensitive on Windows, and CreateProcessW wants the
        // block sorted that way. CompareStringOrdinal(…, TRUE) is the ordinal case-insensitive
        // comparison the loader itself uses — no CRT locale involvement.
        struct EnvNameLess
        {
            bool operator()(const std::wstring& a, const std::wstring& b) const
            {
                return CompareStringOrdinal(a.c_str(), static_cast<int>(a.size()),
                                            b.c_str(), static_cast<int>(b.size()),
                                            TRUE) == CSTR_LESS_THAN;
            }
        };

        // This process's environment with `overrides` applied, in the NUL-separated,
        // double-NUL-terminated, sorted form CREATE_UNICODE_ENVIRONMENT expects.
        std::vector<wchar_t> BuildEnvironmentBlock(
            const std::vector<std::pair<std::string, std::string>>& overrides)
        {
            std::map<std::wstring, std::wstring, EnvNameLess> vars;

            if (LPWCH block = GetEnvironmentStringsW())
            {
                auto freeBlock = wil::scope_exit([&] { FreeEnvironmentStringsW(block); });
                for (const wchar_t* entry = block; *entry; entry += wcslen(entry) + 1)
                {
                    // Entries may legitimately start with '=' (the "=C:" per-drive current
                    // directory pseudo-variables), so the name search starts at index 1.
                    std::wstring_view text(entry);
                    size_t eq = text.find(L'=', 1);
                    if (eq == std::wstring_view::npos)
                        continue;
                    vars[std::wstring(text.substr(0, eq))] = std::wstring(text.substr(eq + 1));
                }
            }

            for (const auto& [name, value] : overrides)
            {
                if (name.empty())
                    continue;
                vars[std::wstring(winrt::to_hstring(name))] = std::wstring(winrt::to_hstring(value));
            }

            std::vector<wchar_t> result;
            for (const auto& [name, value] : vars)
            {
                result.insert(result.end(), name.begin(), name.end());
                result.push_back(L'=');
                result.insert(result.end(), value.begin(), value.end());
                result.push_back(L'\0');
            }
            result.push_back(L'\0'); // the block's own terminator
            return result;
        }
    }

    // Launch `executable` with the given args, capturing stdout (and stderr, merged) as UTF-8.
    // Returns false only if the process could not be started; otherwise exitCode holds the child's
    // status. The executable is resolved via PATH (lpApplicationName = nullptr).
    // When `options.input` is set, its bytes are fed to the child's stdin (then closed for EOF) from
    // a writer thread that runs concurrently with the stdout drain — sequential write-then-read
    // deadlocks as soon as either pipe buffer fills.
    // When `options.onOutput` is set, each chunk is handed to it as it arrives and a heartbeat
    // thread pokes it every 250 ms; a false return from either terminates the child.
    bool WindowsProcessRunner::Run(const std::string& executable,
                                   const std::vector<std::string>& args,
                                   const RunOptions& options,
                                   std::string& out,
                                   int& exitCode) const
    {
        out.clear();
        exitCode = -1;

        try
        {
            SECURITY_ATTRIBUTES sa{};
            sa.nLength = sizeof(sa);
            sa.bInheritHandle = TRUE; // the child's std handles must be inheritable

            // Pipe: the child writes stdout+stderr into writePipe; we read from readPipe.
            wil::unique_handle readPipe, writePipe;
            if (!CreatePipe(readPipe.put(), writePipe.put(), &sa, 0))
                return false;
            SetHandleInformation(readPipe.get(), HANDLE_FLAG_INHERIT, 0); // our read end stays private

            // stdin: a pipe when the caller supplies input, otherwise NUL so the child can
            // never block waiting on input.
            wil::unique_handle stdinRead, stdinWrite;
            wil::unique_hfile nulIn;
            if (options.input.has_value())
            {
                if (!CreatePipe(stdinRead.put(), stdinWrite.put(), &sa, 0))
                    return false;
                SetHandleInformation(stdinWrite.get(), HANDLE_FLAG_INHERIT, 0); // our write end stays private
            }
            else
            {
                nulIn.reset(CreateFileW(L"NUL", GENERIC_READ,
                    FILE_SHARE_READ | FILE_SHARE_WRITE, &sa, OPEN_EXISTING, 0, nullptr));
                if (!nulIn)
                    return false;
            }
            HANDLE childStdin = options.input.has_value() ? stdinRead.get() : static_cast<HANDLE>(nulIn.get());

            // Command line: UTF-8 -> UTF-16 via C++/WinRT, then Win32-quoted.
            std::wstring cmd;
            AppendArg(cmd, winrt::to_hstring(executable));
            for (const auto& a : args)
                AppendArg(cmd, winrt::to_hstring(a));
            std::vector<wchar_t> cmdBuf(cmd.begin(), cmd.end());
            cmdBuf.push_back(L'\0'); // CreateProcessW needs a writable buffer

            // Only build a block when there is something to change; nullptr means "inherit".
            std::vector<wchar_t> envBlock;
            if (!options.env.empty())
                envBlock = BuildEnvironmentBlock(options.env);

            // Restrict inheritance to exactly these two handles.
            HANDLE inheritList[2] = { writePipe.get(), childStdin };

            STARTUPINFOEXW six{};
            six.StartupInfo.cb = sizeof(six);
            six.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            six.StartupInfo.hStdOutput = writePipe.get();
            six.StartupInfo.hStdError = writePipe.get();
            six.StartupInfo.hStdInput = childStdin;

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

            // Cancelling has to kill git's ENTIRE tree, not just git.exe. `git fetch` over HTTP(S)
            // spawns git-remote-http, which inherits the stdout pipe: terminating only the parent
            // leaves the grandchild holding the write end, so ReadFile below would keep blocking
            // until that grandchild finished on its own (measured: a cancel took 21 s instead of
            // one). A job object terminates the whole tree at once. The child is created suspended
            // so it is inside the job before it can spawn anything.
            wil::unique_handle job(CreateJobObjectW(nullptr, nullptr));
            if (job)
            {
                // Also kills the tree if this function unwinds unexpectedly — no orphaned git.
                JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
                limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                SetInformationJobObject(job.get(), JobObjectExtendedLimitInformation,
                                        &limits, sizeof(limits));
            }

            PROCESS_INFORMATION rawPi{};
            const BOOL ok = CreateProcessW(
                nullptr, cmdBuf.data(), nullptr, nullptr, TRUE,
                CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT
                    | CREATE_SUSPENDED,
                envBlock.empty() ? nullptr : envBlock.data(),
                nullptr, &six.StartupInfo, &rawPi);
            if (!ok)
                return false;
            wil::unique_handle childProcess(rawPi.hProcess);
            wil::unique_handle childThread(rawPi.hThread);

            // A failure here is not fatal: the command still runs, and cancellation degrades to
            // terminating git.exe alone (which is what the pre-job behavior was).
            if (job && !AssignProcessToJobObject(job.get(), childProcess.get()))
                job.reset();
            if (ResumeThread(childThread.get()) == static_cast<DWORD>(-1))
                return false;

            writePipe.reset();  // drop our write end so ReadFile sees EOF when the child exits
            stdinRead.reset();  // drop our copy of the child's stdin read end

            // Feed stdin on a separate thread while this thread drains stdout, then close the
            // write end so the child sees EOF (what `commit -F -` waits for). A write failure
            // (child exited early -> broken pipe) just ends the loop; the exit code tells the story.
            std::thread writer;
            if (options.input.has_value())
            {
                // `options` outlives the thread (joined below), so a pointer to its payload is safe.
                const std::string* payload = &*options.input;
                writer = std::thread([payload, stdinWrite = std::move(stdinWrite)]() mutable {
                    size_t off = 0;
                    DWORD written = 0;
                    while (off < payload->size() &&
                           WriteFile(stdinWrite.get(), payload->data() + off,
                                     static_cast<DWORD>(payload->size() - off), &written, nullptr))
                        off += written;
                    stdinWrite.reset(); // EOF for the child
                });
            }

            // The sink may be reached from both this thread and the heartbeat thread, so the
            // interface's "invocations are serialized" promise is kept here, once.
            std::mutex sinkMutex;
            std::atomic<bool> cancelled{ false };
            std::atomic<bool> childExited{ false };

            auto invokeSink = [&](const char* data, size_t length) {
                if (!options.onOutput)
                    return true;
                std::lock_guard<std::mutex> lock(sinkMutex);
                return options.onOutput(data, length);
            };
            auto requestCancel = [&] {
                // exchange() so a race between the reader and the heartbeat only kills once.
                if (cancelled.exchange(true))
                    return;
                if (job)
                    TerminateJobObject(job.get(), 1); // git and everything it spawned
                else
                    TerminateProcess(childProcess.get(), 1); // no job: best effort
            };

            // Heartbeat: gives the sink a chance to cancel a command that has stalled without
            // printing anything (a hung SSH handshake prints nothing at all). Joined below, so
            // childProcess outlives every use of it here.
            std::thread heartbeat;
            if (options.onOutput)
            {
                heartbeat = std::thread([&] {
                    while (!childExited.load(std::memory_order_acquire))
                    {
                        // Slice the wait so joining stays prompt once the child is done.
                        for (int i = 0; i < 5 && !childExited.load(std::memory_order_acquire); ++i)
                            std::this_thread::sleep_for(kHeartbeatInterval / 5);
                        if (childExited.load(std::memory_order_acquire))
                            break;
                        if (!invokeSink(nullptr, 0))
                        {
                            requestCancel();
                            break;
                        }
                    }
                });
            }

            char buf[4096];
            DWORD read = 0;
            while (ReadFile(readPipe.get(), buf, sizeof(buf), &read, nullptr) && read > 0)
            {
                out.append(buf, read);
                if (!invokeSink(buf, read))
                    requestCancel();
            }

            // EOF on the pipe means the child closed its end — stop the heartbeat before touching
            // the process handle again.
            childExited.store(true, std::memory_order_release);
            if (heartbeat.joinable())
                heartbeat.join();
            if (writer.joinable())
                writer.join();

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
