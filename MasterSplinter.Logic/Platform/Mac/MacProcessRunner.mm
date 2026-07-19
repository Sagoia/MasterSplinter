// MacProcessRunner.mm — macOS Adapter implementation (Objective-C++).
//
// The macOS seam deliberately mixes **Foundation + POSIX** (macOS only — no Linux target):
//   * Foundation — NSTask/NSPipe launch and capture git's output (NSTask wraps posix_spawn), using
//                  the modern non-throwing -launchAndReturnError:.
//   * POSIX      — shell-style exit-code semantics: a process killed by a signal reports 128 + signal
//                  (as a POSIX shell would), distinguished via NSTask.terminationReason.
// Compiled only by the macOS toolchain (link Foundation.framework); guarded with `#if defined(__APPLE__)`
// so a non-Apple compiler produces an empty object.
//
// Threading/GCD: intentionally SYNCHRONOUS (blocks until git exits). Running it off the main thread is
// the caller's concern — the Mac frontend owns any Grand Central Dispatch usage — so the Bridge's
// contract stays identical to the Windows adapter. stdout+stderr are merged into one pipe, so a single
// blocking read is correct and deadlock-free.

#if defined(__APPLE__)

#import <Foundation/Foundation.h>

#include "MacProcessRunner.h"

#include <optional>
#include <string>
#include <vector>

namespace ms
{
    bool MacProcessRunner::Run(const std::string& executable,
                               const std::vector<std::string>& args,
                               const std::optional<std::string>& input,
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

            // stdin: a pipe fed with `input` when supplied, otherwise the null device so the
            // child can never block waiting on input.
            NSPipe* stdinPipe = nil;
            if (input.has_value())
            {
                stdinPipe = [NSPipe pipe];
                task.standardInput = stdinPipe;
            }
            else
            {
                task.standardInput = [NSFileHandle fileHandleWithNullDevice];
            }

            // Modern, non-throwing launch (macOS 10.13+): NO on failure instead of an NSException.
            NSError* error = nil;
            if (![task launchAndReturnError:&error])
                return false; // the process could not be started (e.g. env/git missing)

            // Write stdin off-thread while this thread drains stdout — writing and reading
            // sequentially on one thread deadlocks once either pipe buffer fills. Closing the
            // write end gives the child EOF (what `commit -F -` waits for).
            if (stdinPipe != nil)
            {
                NSData* stdinData = [NSData dataWithBytes:input->data() length:input->size()];
                NSFileHandle* stdinHandle = [stdinPipe fileHandleForWriting];
                dispatch_async(dispatch_get_global_queue(QOS_CLASS_UTILITY, 0), ^{
                    @try { [stdinHandle writeData:stdinData]; }
                    @catch (NSException*) { /* child exited early — exit code tells the story */ }
                    [stdinHandle closeFile];
                });
            }

            // Drain the pipe to EOF *before* waiting, so a large diff can't fill the ~64K pipe
            // buffer and deadlock the child. readDataToEndOfFile returns when git closes the pipe.
            NSData* data = [[pipe fileHandleForReading] readDataToEndOfFile];
            [task waitUntilExit];

            // NSData length is authoritative: binary-safe, embedded NULs preserved (image bytes).
            if (data.length > 0)
                out.assign(static_cast<const char*>(data.bytes), data.length);

            // POSIX exit-code semantics: a signal-terminated child reports 128 + signal (shell
            // convention); a normal exit reports its status directly.
            if (task.terminationReason == NSTaskTerminationReasonUncaughtSignal)
                exitCode = 128 + static_cast<int>(task.terminationStatus);
            else
                exitCode = static_cast<int>(task.terminationStatus);
            return true;
        }
    }
}

#endif // defined(__APPLE__)
