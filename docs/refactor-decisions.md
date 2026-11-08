# Safety refactor decisions

Review baseline: `8747a689f54c086cb24405d42113fcdfbec79103`.

This log groups the reviewed problems by the design rule that addresses them.
Implementation and verification results are recorded separately; a decision is
not evidence that a Windows operation has been tested.

Project references remain one-way: the app references Core and Tray, the CLI and
tests reference Core, and neither Core nor Tray references the app. Moving files
between projects is not needed to correct the reviewed lifecycle problems.

## Keep durable originals separate from attempt rollback

The first saved original belongs to the complete apply/revert lifecycle. An
attempt snapshot belongs only to one operation. Repeating an apply must not
replace the original. A failed attempt must restore its starting state without
discarding the original. Defaults are used only when there is no saved original.

- Registry reapply replaced saved originals.
- Registry revert could replace a successfully restored original with a default.
- Service revert ignored an original equal to the target start type.
- Nagle and other multi-value operations could leave earlier writes applied.
- Several catalog entries changed the same registry values under separate ids.
- Power and CPU boost revert used defaults rather than saved originals.

## Persist recovery state before changing Windows

Save a candidate state before publishing it in memory. Coordinate a file's
read/modify/write operation across store instances and processes. Atomic file
replacement alone does not prevent stale writers from losing another update.

- Backup stores modified their cached state before a save could fail.
- Separate app and CLI stores could overwrite each other's cached snapshots.
- Scheduled tasks changed before their originals were durably recorded.
- Update pause wrote several values before recording ownership.

## Give temporary changes one recovery owner

The code responsible for stopping a service or temporarily changing a setting
must also recover it on failure. Record that responsibility before requesting a
stop, reconcile delayed completion, and include recovery failures in the result.

- Update reset could exit between stopping and restarting services.
- A stop timeout could leave a service stopping without a restart owner.
- Cleanup ignored service restart failures.
- Killing a restore-point child process could bypass its registry restoration.
- DNS failure could leave earlier adapters or servers changed.

## Define the cleanup boundary during traversal

Scanning and deletion must share a traversal rule that excludes linked roots
and reparse points. A path below a selected directory is not necessarily stored
inside that directory.

- Recursive cache discovery and deletion could follow a junction outside the
  selected target.

## Separate rendering from user intent

Rendering a state snapshot must never execute a Windows change. Older snapshots
must not replace newer operation results. Each operation owns its busy state and
captures its inputs before awaiting work.

- Updating a bound tweak switch could trigger apply or revert.
- A refresh could clear another operation's busy flag.
- Changing a picker during work could make the result describe another choice.
- Clearing a profile preview during apply invalidated its in-flight input.
- Startup and manual update checks could start overlapping downloads.

## Keep one authoritative state and derive projections

- Provisioned-app removal updated the filtered list but not its source.
- An empty cleanup selection was read as a missing preference.
- Initial theme application ran before the window reference existed.

## Distinguish unknown state from an empty result

- Failed winget exports looked like successful reads with no installed apps.
- Profile exports could therefore save incomplete selections without a failure.
- Profile deserialization accepted explicit null collections that later crashed
  counting or planning.
- Failed tweak probes looked like disabled or unsupported tweaks and could make
  a partial score appear complete. Unknown rows now carry a read error and are
  excluded from the score denominator; an incomplete score is labelled as such.

## Keep tests tied to behavior

Retain regression coverage while changing production code. Prefer observable
results over private-field reflection, unique temporary paths over fixed names,
and per-entry output assertions over a match that another entry can satisfy.
Keep comments explaining external requirements; remove implementation restatements
only in code being changed.

## Regression evidence

- Baseline: the Release solution build had zero warnings and errors, and all 54
  existing tests passed. Those tests did not exercise the reviewed failure paths.
- An isolated power-plan name test failed with `FormatException` before name
  resolution was added. Four power-plan tests then passed using fake commands.
- An isolated failed-probe score test expected `Unavailable` but received
  `Not optimized` before unknown-state handling. Five score tests then passed.
- First integration checkpoint: `dotnet build Stratus.sln -c Release` succeeded with zero warnings
  and errors. `dotnet test Stratus.Tests\Stratus.Tests.csproj -c Release --no-build`
  passed 209 tests, with zero failures or skips.
- The non-elevated CLI smoke check, `dotnet run --project Stratus.Cli -c Release
  --no-build -- score`, printed the score and all six categories.
- No elevated Windows operation has been exercised by these isolated tests.
- Previous integration checkpoint: the Release solution build succeeded with zero warnings
  and errors. The Release test command above passed 241 tests, with zero failures
  or skips. The non-elevated CLI score command also completed successfully.
- CPU boost tests reproduced both failed activation leaving changed values and
  failed revert refresh losing the original backup. Both regression tests passed
  after correction. Profile cleanup tests reproduced replacement of an existing
   selection and passed after the import became additive.
- After review resumed, two isolated power-plan query tests failed because command
  failures were returned as empty state. Both now report read errors, and all ten
   power-plan tests pass with fake commands.
- Final bounded integration: `dotnet build Stratus.sln -c Release` succeeded with
  zero warnings and errors. The Release test command passed 295 tests, with zero
  failures or skips. The non-elevated CLI score smoke check completed successfully.

## Review limits

The resumed independent pass and its assigned corrections are finished. At the
maintainer's request, scope was then closed rather than starting another review
round. This is not a claim that every structural issue has been eliminated.

Service discovery now distinguishes failed reads from absent services, and saved
service originals preserve delayed automatic startup. Real service restoration
still needs the elevated checks below. A failed stop while disabling a service
can still report success for its changed start type without separately warning
that it remains running. Store mutexes are session-local: coordination across
different Windows logon sessions is not guaranteed. Some bulk-operation probes
still treat unreadable state as false; further changes are deferred.

DNS restore keeps saved originals when either address family cannot be restored.
An adapter with IPv6 disabled may therefore need manual verification after a
reported restore failure. A failed IPv6 command alone does not prove that IPv6
is disabled, so its saved state is not discarded automatically.

## Windows checks before release

Run these checks in an elevated disposable VM. Isolated tests with fake service
and process implementations do not prove Windows COM, netsh or WinUI behavior.

1. Put a junction inside a selected cleanup directory, pointing to a sibling
   directory with a marker file. Scan and clean; the marker must survive. Repeat
   with the selected root itself a junction.
2. Start with non-default registry values, service start types, power settings
   and DNS servers. Apply twice and revert; compare against the exact originals.
   Make a later write fail and confirm earlier writes return to the state at the
   start of that attempt, while the original backup remains available.
3. Pause and resume Windows Update. Change the pause expiry outside Stratus;
   Resume must refuse to overwrite it. Reset the update cache and verify that
   every previously running service returns, including a slow stop.
4. Delay a Tweaks refresh, apply a tweak, then let the refresh finish. No refresh
   or bound switch update may execute another apply or revert.
5. Start a long Tools action and try Refresh and another action. Change a DNS or
   power selection while work runs; the reported result must name the input
   actually used. Try clearing a profile preview during apply.
6. Remove a provisioned app, change the search filter and confirm it stays gone.
   Select no cleanup targets and restart. Choose a theme different from Windows
   and restart; verify the first window uses the saved theme.
7. Start an update check while the startup check is pending. Confirm only one
    check/download owns the update state and failures allow a later retry.
8. Keep the app open while a CLI profile import changes one scheduled task. Then
   change a different task in the app. Both saved originals must remain available
   after restarting the app, and both tasks must reset to their original states.
9. Apply and revert a delayed automatic service in the disposable VM and compare
   both Start and DelayedAutoStart before and after. Revert a never-applied service
   already outside the tweak's target state; its configuration must not change.
