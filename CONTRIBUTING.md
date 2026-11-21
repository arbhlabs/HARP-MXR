# Contributing

Thanks for helping. MixDock is small, so the rules are short.

- **One change per pull request**, with a sentence on why. Bug reports: say which OS, which build and how to reproduce it.
- **Run the tests for the part you touched** before you open the PR:
  - Windows / shared DSP: `dotnet run -c Release --project windows/tests/MixDock.<Suite>Tests/MixDock.<Suite>Tests.csproj` (suites: Dsp, Gamepad, Waveform, Pad, Sync, Routing, AudioTiming, Remote)
  - macOS: `cd macos && swift test`
  - Linux: `dotnet run --project linux -- --selftest`
- **Keep the audio thread safe:** no allocations, locks held for long, or exceptions in render callbacks.
- **Controller changes:** update the mapping tables in the README and `windows/docs/MAPPINGS.md`, and the hint text in `PadHints` (it is unit-tested).
- **Don't commit** personal paths, device names, tokens or recordings. CI and reviewers will ask you to remove them.
- By contributing you agree your work is released under the MIT license in [LICENSE](LICENSE).
