---
name: companion
description: Show or hide the local Codex Companion, switch its character or language, refresh its read-only Codex quota, or inspect its memory and status. Use only for this desktop companion, not real pets.
---

Resolve ../../bin/CodexCompanion.Cli.exe relative to this skill directory. Invoke this small console client directly with literal arguments; it returns JSON and exits reliably. It starts the GUI companion when needed. No PowerShell script or policy override is needed.

| Request | Arguments |
| --- | --- |
| Show or start | --command show |
| Hide | --command hide |
| Exit | --command exit |
| Astra | --command set-character --value elegant |
| Sol | --command set-character --value mischievous |
| Luna | --command set-character --value cute |
| Fable | --command set-character --value fable |
| Haiku | --command set-character --value haiku |
| Pro | --command set-character --value pro |
| Flash | --command set-character --value flash |
| Chinese | --command set-language --value zh-CN |
| English | --command set-language --value en |
| Japanese | --command set-language --value ja |
| Refresh real quota | --command refresh-quota |
| Set refresh interval in seconds (5–3600) | --command set-refresh-interval --value 60 |
| Inspect status and memory | --command get-status |

The app accepts only these fixed values. A stopped get-status or hide returns an explanatory JSON error without starting the app. Settings commands start it if necessary. Use only character and language values requested by the user. Never evaluate user text as a shell command.

Report percentages only from returned data, including freshness and update time. Missing or unavailable windows are unknown. Never open, copy, print, or request login secrets. Startup prefetches quota asynchronously. The app refreshes in the background at the user's interval (default30seconds), and after each cached reset deadline. Hidden state still refreshes but releases its temporary connection after each read; visible idle connections release after about10seconds. Snapshot display is immediate memory access, not a guarantee of a fresh network round trip below150ms.

The refresh-quota reply acknowledges a requested refresh and may contain the previous snapshot. Record its updatedAt, then poll get-status about once per second for at most 30 seconds. Report a fresh reading only after updatedAt changes with live status. If the result becomes stale/unavailable/notLoggedIn, report that state. If the timestamp does not change before the bound, say the refresh is still pending and label any displayed reading with its earlier timestamp.

If referencePreview is true, original reference illustrations are being displayed, and finished transparent expression art is not installed. Do not describe these references as finished animation assets.

Quota reset reminders are enabled by default and can be disabled in the companion's right-click menu. They only announce an observed same-account transition from actual 0% to 100%, never an inferred reset. Status includes quotaResetNotifications. A failed read includes failureKind and retryAt; the app reconnects after timeouts with 5/15/60-second backoff. Cached readings remain labelled stale with their original timestamp and are restored only after the same account is verified. Do not claim the external quota service is always available.

If bin/CodexCompanion.Cli.exe or CodexCompanion.exe is missing, install the complete marketplace package. Do not run or restore quarantined scripts, alter antivirus settings, or override PowerShell execution policy.
