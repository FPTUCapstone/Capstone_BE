# Travel group host management implementation

Approved by the current request; use this worktree and preserve all existing changes.

1. Add guarded domain rename, command/body/response and name validation; serialize the authorized update and audit write with the existing group lock.
2. Add an explicitly restricted dissolve command and endpoint; authorize the Active host, report a clear 409, and perform no writes.
3. Map the new conflict code, inspect the focused diff against the contract and review lifecycle/access safety. No test/build/format/lint commands or remote operations, per user instruction.
