// MacProcessRunner.mm — macOS Adapter implementation (Objective-C++).
//
// Uses Apple's Foundation framework (NSTask + NSPipe) to launch git and capture its output — the
// idiomatic macOS way to run a subprocess, replacing generic POSIX fork/exec. Compiled only by the
// macOS toolchain (link Foundation.framework); the whole TU is guarded with `#if defined(__APPLE__)`
// so a non-Apple compiler produces an empty object.
//
// Note on threading / GCD: this Adapter is intentionally SYNCHRONOUS — it blocks until git exits.
// Running it off the main thread is the caller's concern (just as the Windows app dispatches these
// via Task.Run), so the Mac frontend owns any Grand Central Dispatch usage. Keeping the runner
// synchronous keeps the Bridge's contract identical on both platforms. The one place GCD would live
// *inside* here is if stdout and stderr were separate pipes drained concurrently — we merge them
// into one pipe (like the Windows runner), so a single blocking read is correct and deadlock-free.

#if defined(__APPLE__)

#import <Foundation/Foundation.h>

#include "MacProcessRunner.h"

#include <string>
#include <vector>

namespace ms
{
    bool MacProcessRunner::Run(const std::string& executable,
                               const std::vector<std::string>& args,
                               std::string& out,
                               int& exitCode) const
    {
        out.clear();
        exitCode = -1;

        @autoreleasepool
        {
            NSTask* task = [[NSTask alloc] init];

            // Resolve the executable via PATH by launching through /usr/bin/env, mirroring the
            // "git is found on PATH" contract the Windows runner relies on (executable == "git").
            task.launchPath = @"/usr/bin/env";

            NSMutableArray<NSString*>* argv = [NSMutableArray array];
            [argv addObject:[NSString stringWithUTF8String:executable.c_str()]];
            for (const auto& a : args)
                [argv addObject:[NSString stringWithUTF8String:a.c_str()]];
            task.arguments = argv;

            // Merge stdout + stderr into one pipe (same contract as the Windows runner).
            NSPipe* pipe = [NSPipe pipe];
            task.standardOutput = pipe;
            task.standardError = pipe;
            task.standardInput = [NSFileHandle fileHandleWithNullDevice];

            @try
            {
                [task launch];
            }
            @catch (NSException*)
            {
                return false; // the process could not be started (e.g. env/git missing)
            }

            // Drain the pipe to EOF *before* waiting, so a large diff can't fill the ~64K pipe
            // buffer and deadlock the child. readDataToEndOfFile returns when git closes the pipe.
            NSData* data = [[pipe fileHandleForReading] readDataToEndOfFile];
            [task waitUntilExit];

            // NSData length is authoritative: binary-safe, embedded NULs preserved (image bytes).
            if (data.length > 0)
                out.assign(static_cast<const char*>(data.bytes), data.length);
            exitCode = static_cast<int>(task.terminationStatus);
            return true;
        }
    }
}

#endif // defined(__APPLE__)
