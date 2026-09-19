---
name: make-publish-build
description: "Publish and relaunch Pagurian through build-publish.bat only when the user explicitly invokes $make-publish-build. Never activate for ordinary code changes or implicit build requests."
---

# Publish and relaunch Pagurian

Run this workflow only in a turn where the user explicitly invokes
`$make-publish-build`. Do not infer it from code changes, build requests, release
requests, earlier invocations, or prior turns. An invocation applies only to the
current turn.

After all requested repository edits for the current turn are complete:

1. Work from the Pagurian repository root and resolve
   `publish\Pagurian.exe` to an absolute path.
2. If the binary exists, first try to delete that exact file with PowerShell
   `Remove-Item -LiteralPath`.
3. If deletion fails because the file is locked, find only processes whose
   executable `Path` exactly equals the resolved binary path. Terminate those
   processes, wait for them to exit, and retry deleting the file. Never kill by
   process name alone or terminate an executable at another path. If no exact
   owner is found or deletion still fails, stop and report the error.
4. Run `build-publish.bat` from the repository root. Do not pass a build
   configuration unless the user requested one. Capture its output and exit
   code.
5. If the build fails, stop and report the failure. Do not launch an old or
   partial publish output.
6. On success, verify that `publish\Pagurian.exe` exists. Launch it with the
   `publish` directory as its working directory, then verify that the started
   process remains running and its executable path matches the resolved path.
7. Report whether a previous binary or process was removed, the publish result,
   the executable path, and the new process ID.

Run the workflow once after the turn's edits, not after every intermediate
patch. If the user explicitly invokes the skill only to build and start the
program, run it immediately.
