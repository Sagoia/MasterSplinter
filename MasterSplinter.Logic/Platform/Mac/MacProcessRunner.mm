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
// blocking read loop is correct and deadlock-free.

#if defined(__APPLE__)

#import <Foundation/Foundation.h>

#include "MacProcessRunner.h"

#include <atomic>
#include <chrono>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <vector>

namespace ms
{
    namespace
    {
        // Matches the Windows adapter: small enough that Cancel feels immediate, large enough to
        // cost nothing.
        constexpr auto kHeartbeatInterval = std::chrono::milliseconds(250);
    }

    bool MacProcessRunner::Run(const std::string& executable,
                               const std::vector<std::string>& args,
                               const RunOptions& options,
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

            // Environment: inherit ours, then apply the caller's overrides. Leaving
            // task.environment nil would also inherit, but only wholesale — there would be no
            // way to add GIT_TERMINAL_PROMPT=0 for the network commands.
            if (!options.env.empty())
            {
                NSMutableDictionary<NSString*, NSString*>* env =
                    [[[NSProcessInfo processInfo] environment] mutableCopy];
                for (const auto& kv : options.env)
                {
                    if (kv.first.empty())
                        continue;
                    env[[NSString stringWithUTF8String:kv.first.c_str()]] =
                        [NSString stringWithUTF8String:kv.second.c_str()];
                }
                task.environment = env;
            }

            // Merge stdout + stderr into one pipe (same contract as the Windows runner).
            NSPipe* pipe = [NSPipe pipe];
            task.standardOutput = pipe;
            task.standardError = pipe;

            // stdin: a pipe fed with `input` when supplied, otherwise the null device so the
            // child can never block waiting on input.
            NSPipe* stdinPipe = nil;
            if (options.input.has_value())
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
                NSData* stdinData = [NSData dataWithBytes:options.input->data()
                                                   length:options.input->size()];
                NSFileHandle* stdinHandle = [stdinPipe fileHandleForWriting];
                dispatch_async(dispatch_get_global_queue(QOS_CLASS_UTILITY, 0), ^{
                    @try { [stdinHandle writeData:stdinData]; }
                    @catch (NSException*) { /* child exited early — exit code tells the story */ }
                    [stdinHandle closeFile];
                });
            }

            // The sink may be reached from both this thread and the heartbeat thread, so the
            // interface's "invocations are serialized" promise is kept here, once.
            std::mutex sinkMutex;
            std::atomic<bool> cancelled{ false };
            std::atomic<bool> childExited{ false };

            auto invokeSink = [&](const char* data, std::size_t length) {
                if (!options.onOutput)
                    return true;
                std::lock_guard<std::mutex> lock(sinkMutex);
                return options.onOutput(data, length);
            };
            auto requestCancel = [&] {
                if (!cancelled.exchange(true))
                    [task terminate];
            };

            // Heartbeat: gives the sink a chance to cancel a command that has stalled without
            // printing anything (a hung SSH handshake prints nothing at all).
            std::thread heartbeat;
            if (options.onOutput)
            {
                heartbeat = std::thread([&] {
                    while (!childExited.load(std::memory_order_acquire))
                    {
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

            // Drain the pipe to EOF *before* waiting, so a large diff can't fill the ~64K pipe
            // buffer and deadlock the child. -availableData blocks until bytes arrive and returns
            // an empty NSData at EOF, which is what ends the loop.
            NSFileHandle* reader = [pipe fileHandleForReading];
            for (;;)
            {
                NSData* chunk = nil;
                @try { chunk = [reader availableData]; }
                @catch (NSException*) { break; } // pipe torn down (e.g. by terminate)
                if (chunk.length == 0)
                    break;
                // NSData length is authoritative: binary-safe, embedded NULs preserved (image bytes).
                out.append(static_cast<const char*>(chunk.bytes), chunk.length);
                if (!invokeSink(static_cast<const char*>(chunk.bytes), chunk.length))
                    requestCancel();
            }

            childExited.store(true, std::memory_order_release);
            if (heartbeat.joinable())
                heartbeat.join();

            [task waitUntilExit];

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
