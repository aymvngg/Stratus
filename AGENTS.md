# AGENTS.md

Working rules for anyone, human or AI, changing this repository. Read `SPEC.md` first for the architecture.

## Build and test

```
dotnet build Stratus.sln -c Release
dotnet test Stratus.Tests\Stratus.Tests.csproj -c Release --no-build
```

Both must pass before a commit. CI runs the same commands plus a CLI smoke test. Do not commit a build with warnings.

## Style

- ASCII only. No em dashes, no curly quotes, no Unicode arrows. Use `->` when you need an arrow.
- Comments explain why, not what. Delete a comment that repeats the code.
- UI strings are plain English, sentence case, no exclamation marks, no marketing.
- Match the surrounding code. Do not reformat files you are not changing.
- Every async flow needs a `catch` that logs and reports a message. Never swallow an exception silently.

## Safety invariants

These are not optional:

1. Every registry write goes through `RegistryService`, which backs up first. Never call `Registry.SetValue` directly for a tweak.
2. Every service change goes through `ServiceTweak`, which records the original start type.
3. `IsApplied` reads live system state, never a Stratus JSON file.
4. Revert restores the backup. A hardcoded default is only a fallback when no backup exists.
5. A failed multi-value apply must roll back what it wrote. A failed revert must keep the backup so it can be retried.
6. Advanced risk means it can break a feature. Say so in the description.
7. No downloaded scripts and no config-file code execution. Execution stays compiled in C#.
8. No change to Defender, BitLocker, IPv6 preference, hosts files or third-party script runners. These were rejected deliberately.

## Adding things

- A tweak is one entry in `TweakCatalog.Build`, inside its category section, with a unique kebab-case id, a description that states the effect and that revert exists, a `Weight` and a `Risk`.
- A preset is one entry in `PresetCatalog`, included in `All`. Every id must resolve; `TweakCatalogTests` enforces it.
- A DNS provider is one `DnsProfile` in `DnsService.Profiles`.
- A cleanup target is one `CleanupTarget` in `CleanupService.Targets`. Prefer scoped `CacheDirNames` or `FileGlobs` over deleting a tree.
- A new Windows feature goes in `OptionalFeatureService` with a friendly name, and must work for both removal and enablement.
- A new store must use `JsonFile`. Do not hand roll file IO.
- A new page needs a view model, an `IsBusy` guard on long actions, and severity that follows the real result.

## File ownership

When several agents work in parallel, each one owns a disjoint file set and must not edit files outside it. State the owned files in the task. Integration and builds happen once, after all agents report.

## Commits

- One logical change per commit. Message: a short imperative subject, then a body with the concrete changes.
- Do not push, tag or publish unless explicitly asked.
- Do not commit generated output: `Releases/`, `publish/`, `bin/`, `obj/` are ignored.
- Update `CHANGELOG.md` under the next version for anything a user can see.

## Verification

The maintainer tests every change manually in the app. When you finish, report:

- what changed, with file paths
- what you could not verify without an elevated session or a specific Windows build
- the exact manual steps to check it
- any known risk or accepted quirk

Do not claim something works because it compiles. Say what was actually run.

## PowerShell and Windows specifics

- The app runs elevated; the development shell often does not. DISM, feature and service operations only work at runtime.
- Never parse localized command output. Use the COM API, registry or a language-neutral format.
- Prefer `ProcessRunner` over `System.Diagnostics.Process` so timeouts and encoding stay consistent.
- French Windows is a supported target: keep messages ASCII and decode child output as UTF-8.
- `git push` prints a harmless `NativeCommandError` wrapper on success. Verify with `gh run list` instead of trusting the exit code.
- In PowerShell, `gh --jq` breaks on expressions with spaces. Use space-free expressions.
