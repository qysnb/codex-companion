---
name: companion
description: Show or hide the local Codex Companion, switch its character or language, explicitly refresh its read-only Codex quota online, or inspect cached status. Use only for this desktop companion, not real pets.
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
| Explicitly refresh quota online | --command refresh-quota |
| Set interval preference in seconds (5–3600) | --command set-refresh-interval --value 900 |
| Inspect cached status and memory | --command get-status |

The app accepts only these fixed values. A stopped get-status or hide returns an explanatory JSON error without starting the app. Settings commands start it if necessary. Use only character and language values requested by the user. Never evaluate user text as a shell command.

## Network policy

Version0.6.0 defaults to **local event sync, manual network only**, including older settings with no network-mode field. Startup, normal clicks, hiding, retry deadlines and cached reset timestamps do not initiate quota reads in this mode. Inspecting status is local. Do not automatically refresh merely because a cache is absent, stale or unknown; explain that the user can choose Refresh quota online. An explicit request for a fresh online reading permits one refresh-quota call.

The user can select the network mode in the character's right-click menu:

- `local`: the app subscribes read-only to new host quota events and clicks display memory cache; only explicit refresh-quota or Refresh quota online uses the network.
- `on-demand`: a quota-page click may read online, with at least max(900seconds, configured refresh interval) between attempts, including failed attempts. No background polling.
- `automatic`: explicitly selected prefetch, periodic reads, retries and cached-reset reads. Hidden state still refreshes. New interval preference defaults to900seconds; previous user values are preserved.

Changing the interval does not enable automatic mode. Status includes `quotaNetworkMode` and `refreshIntervalSeconds`. Do not enable automatic networking or invoke repeated refreshes without a user request. The private App Server child disables unrelated plugin, skill, MCP, model-discovery and analytics initialization without changing desktop Codex's global configuration. Online reads still incur traffic; do not promise a fixed byte cost or zero traffic after a manual refresh.

Local event subscription is enabled by default and can be disabled in the right-click menu. It reads only quota events from CODEX_HOME/sessions, does not replay pre-attachment quota, does not parse or output message bodies, and does not query the network. Auth-file metadata changes invalidate the cache; credential contents are never read. Internal event formats can change. Do not repeatedly call get_usage_limits or refresh-quota to simulate a subscription. No host event means no new quota knowledge.

Status includes quotaSource (local-session/app-server/none), localQuotaSync, and localQuotaSubscription statistics. A local-session snapshot is an observed host event, not a verified account identity; do not claim it is an account-verified online read or that it can produce account-verified reset reminders. Whole snapshots remain separate across sources.

## Reporting quota

Report percentages only from returned data with the original `updatedAt`. A cached snapshot, even if its status remains live from its last successful read, is not proof of a fresh network response. Missing windows are unknown. Never open, copy, print or request login secrets. Cache is memory-only and empty after restart until a new local event or explicit online read; do not fabricate persisted values or infer100% from a reset countdown. Snapshot display is immediate memory access, not a guarantee of a fresh network round trip below150ms.

The refresh-quota reply acknowledges a requested online refresh and may contain the previous snapshot. Record its updatedAt, then poll get-status about once per second for at most30seconds; these status polls do not initiate quota requests. Report a fresh reading only after updatedAt changes with live status. If the result becomes stale/unavailable/notLoggedIn, report that state. If the timestamp does not change before the bound, say the refresh is still pending and label any displayed reading with its earlier timestamp. Do not repeatedly issue refresh-quota as a polling method.

If referencePreview is true, original reference illustrations are being displayed, and finished transparent expression art is not installed. Do not describe these references as finished animation assets.

Quota reset reminders are enabled by default and can be disabled in the character's right-click menu. They only announce an observed same-account transition from actual0% to100%, never an inferred reset. Local mode needs a subsequent explicit online read to observe changes; on-demand mode also does not guarantee a reminder at the expected reset time. Status includes quotaResetNotifications. Failures include failureKind and retryAt; only automatic mode retries on the5/15/60-second schedule. Old account caches are restored only after the same account is verified. Do not claim the external quota service is always available.

If bin/CodexCompanion.Cli.exe or CodexCompanion.exe is missing, install the complete marketplace package. Do not run or restore quarantined scripts, alter antivirus settings, or override PowerShell execution policy.
