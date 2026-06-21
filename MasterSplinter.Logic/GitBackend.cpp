// GitBackend.cpp : Read-only git backend for the cross-platform core.
//
// Following TortoiseGit's model (its CGit class shells out to git.exe and parses a
// delimited stream — see TortoiseGit/src/Git/Git.cpp CGit::GetLogCmd), this layer builds
// git command lines and runs git.exe, returning the raw (UTF-8) output to the caller.
// The C# app parses the delimited output into view models via the flat C ABI below.
//
// Only RunGit() is Windows-specific (CreateProcessW). Keeping it isolated here means a
// POSIX fork/exec implementation can replace it later behind the same signature, so the
// command-building logic above it stays portable (no <windows.h> dependency).

#include "pch.h"
#include "MasterSplinter.Logic.h"

#include <string>
#include <vector>
#include <cstdlib>
#include <cstring>

namespace
{
    // Field/record separators. git's --pretty=format and -z emit these bytes literally;
    // they never occur in normal commit text, so parsing in C# is unambiguous.
    constexpr char US = '\x1f'; // unit separator   -> between fields
    constexpr char RS = '\x1e'; // record separator -> between commits

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

    // Launch git with the given args, capturing stdout (and stderr, merged) as UTF-8.
    // Returns false only if the process could not be started; otherwise exitCode holds
    // git's status. git.exe is resolved via PATH (lpApplicationName = nullptr).
    bool RunGit(const std::vector<std::string>& args, std::string& out, int& exitCode)
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
        AppendArg(cmd, L"git");
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

    // Run a repository-scoped command: git -C <root> <args...>
    std::string RunGitC(const std::string& root, std::vector<std::string> args, int& code)
    {
        std::vector<std::string> full = { "-C", root };
        full.insert(full.end(), args.begin(), args.end());
        std::string out;
        if (!RunGit(full, out, code))
        {
            code = -1;
            out.clear();
        }
        return out;
    }

    // Heap copy the caller frees via MsGitFree (allocated inside this DLL, freed inside it).
    char* DupString(const std::string& s)
    {
        char* p = static_cast<char*>(malloc(s.size() + 1));
        if (!p)
            return nullptr;
        memcpy(p, s.data(), s.size());
        p[s.size()] = '\0';
        return p;
    }

    void TrimTrailingNewlines(std::string& s)
    {
        while (!s.empty() && (s.back() == '\n' || s.back() == '\r'))
            s.pop_back();
    }
}

// ------------------------------------------------------------------------------------------
// Flat C ABI (declared in MasterSplinter.Logic.h, consumed by C# via P/Invoke).
// ------------------------------------------------------------------------------------------

extern "C" MASTERSPLINTERLOGIC_API bool MsGitIsRepository(const char* path)
{
    if (!path || !*path)
        return false;
    int code;
    std::string out = RunGitC(path, { "rev-parse", "--is-inside-work-tree" }, code);
    TrimTrailingNewlines(out);
    return code == 0 && out == "true";
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitOpenRepository(const char* path)
{
    if (!path || !*path)
        return DupString(std::string("ERR") + US + "No folder was provided");

    int code;
    std::string top = RunGitC(path, { "rev-parse", "--show-toplevel" }, code);
    TrimTrailingNewlines(top);
    if (code != 0 || top.empty())
        return DupString(std::string("ERR") + US + "The selected folder is not a Git repository");

    int code2;
    std::string branch = RunGitC(path, { "rev-parse", "--abbrev-ref", "HEAD" }, code2);
    TrimTrailingNewlines(branch);
    if (branch == "HEAD") // detached HEAD -> show the short hash instead
    {
        int code3;
        std::string sh = RunGitC(path, { "rev-parse", "--short", "HEAD" }, code3);
        TrimTrailingNewlines(sh);
        branch = sh.empty() ? std::string("(detached)") : "(detached " + sh + ")";
    }
    else if (branch.empty())
    {
        branch = "(no commits yet)";
    }

    return DupString(std::string("OK") + US + top + US + branch);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitLog(const char* root, int order, int maxCount)
{
    if (!root || !*root)
        return DupString(std::string());

    const char* orderFlag = "--date-order";
    bool reverse = false;
    switch (order)
    {
    case 1: orderFlag = "--topo-order"; break;
    case 2: orderFlag = "--date-order"; reverse = true; break; // "Reverse Date Order"
    case 3: orderFlag = "--author-date-order"; break;
    default: orderFlag = "--date-order"; break;
    }

    // full %H, short %h, parents %P, author name/email/ISO date, committer name/email/ISO date,
    // ref decorations %D (for description badges), subject %s, body %b. Records end with RS.
    const std::string fmt =
        "--pretty=format:%H%x1f%h%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%cn%x1f%ce%x1f%cI%x1f%D%x1f%s%x1f%b%x1e";

    std::vector<std::string> args = { "log", "--all", "--parents", orderFlag };
    if (reverse)
        args.push_back("--reverse");
    if (maxCount > 0)
        args.push_back("-n" + std::to_string(maxCount));
    args.push_back(fmt);

    int code;
    std::string out = RunGitC(root, args, code);
    return DupString(out);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRefs(const char* root)
{
    if (!root || !*root)
        return DupString(std::string());
    // One ref per line (the C# side splits on newline). NOTE: do NOT use NUL separators here
    // or anywhere a char* is returned -- the managed marshaller stops at the first NUL byte.
    int code;
    std::string out = RunGitC(root,
        { "for-each-ref", "--format=%(refname)", "refs/heads", "refs/tags", "refs/remotes" },
        code);
    return DupString(out);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitCommitFiles(const char* root, const char* sha)
{
    if (!root || !sha || !*sha)
        return DupString(std::string());
    // --first-parent -m: for merges, show changes vs the first parent (Phase 1 simple).
    // --root: the initial commit lists its files instead of being empty.
    // Output is line-based "status<TAB>path" (entries separated by newlines). We deliberately
    // do NOT use -z: a NUL-separated payload would be truncated at the first NUL by the managed
    // string marshaller. core.quotePath=false keeps non-ASCII paths literal.
    int code;
    std::string out = RunGitC(root,
        { "-c", "core.quotePath=false", "diff-tree", "--no-commit-id", "-r", "-M", "--root",
          "--first-parent", "-m", "--name-status", sha },
        code);
    return DupString(out);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitFileDiff(const char* root, const char* sha, const char* path)
{
    if (!root || !sha || !*sha || !path || !*path)
        return DupString(std::string());
    int code;
    std::string out = RunGitC(root,
        { "diff-tree", "-p", "-M", "--first-parent", "--root", "--no-commit-id", "--no-color",
          sha, "--", path },
        code);
    return DupString(out);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitFileAtCommit(const char* root, const char* sha, const char* path)
{
    if (!root || !sha || !*sha || !path || !*path)
        return DupString(std::string());
    int code;
    std::string out = RunGitC(root, { "show", std::string(sha) + ":" + path }, code);
    if (code != 0)
        return DupString(std::string());
    return DupString(out);
}

extern "C" MASTERSPLINTERLOGIC_API void MsGitFree(char* ptr)
{
    free(ptr);
}
