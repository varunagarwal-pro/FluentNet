## Summary
<!-- Describe what this PR changes and why. -->

## Linked Issue / Task
<!-- e.g. Closes #12 / Tasks.md Task 7 -->

## Architecture & Safety Checklist
- [ ] Strictly 64-bit (`x64` / `arm64`, no `AnyCPU` or `x86`)
- [ ] All probes, CIM queries, and ICMP diagnostics run asynchronously off the UI thread
- [ ] Pre-change JSON snapshot and rollback path preserved for any system mutation
- [ ] `FluentNet.Core` compiles cleanly for both `.NET 10` (`NativeAOT`) and `.NET 4.8` (`FluentNet.Classic` linked `.cs`)
- [ ] UI remains `asInvoker` (no always-admin manifest)
- [ ] All `xUnit` tests pass locally (`dotnet test -c Release`)
- [ ] UNIX LF (`\n`) line endings preserved
