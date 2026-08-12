# Case Study: Abnormally Large (2GB) NTUSER.DAT — RDP Client Diagnostic Cache Bloat

## Summary

A real-world `NTUSER.DAT` hive grew to **2,147,483,648 bytes (exactly 2GB)**,
large enough to trip the .NET 2GB single-array allocation limit and require
this project's `--exceeds2GbRecovery` mode just to load the file at all.
After adding that recovery mode, the natural follow-up question was: *is
this file actually corrupted, or is it just... really, genuinely this big?*

**Conclusion: the hive is not corrupted.** It is legitimately this large
because of a single key,
`HKCU\Software\Microsoft\RdClientRadc\DiagConnectionCache`, which had
accumulated **1,014,294 subkeys** — effectively unbounded growth in what
appears to be a Windows Remote Desktop (RDP) client diagnostic/connection
cache that is never pruned by the client itself. This single key accounts
for essentially the entire size of the hive.

This document records the methodology used to reach that conclusion (which
is now productized as reusable analysis code — see
`Registry/Analysis/HiveAnalyzer.cs`), the findings, and the remediation
approach (`--pruneKey` / `--compact` in `RegistryToolkit`) for cleaning up a
hive affected by this same issue in the future.

## Why a 2GB registry hive is inherently suspicious

Typical `NTUSER.DAT` hives are a few tens of MB; a "big" one (heavy browser
history, deeply customized shell settings, years of use) might reach a
couple hundred MB. A hive at the 2GB boundary is **10-100x** larger than
even a heavily-used normal hive, so the working assumption going in was that
this was either (a) genuine corruption/duplication, or (b) a single
misbehaving application creating enormous numbers of keys/values. The
analysis below was designed to distinguish between these.

## Methodology

All of the following is now implemented as reusable, documented methods in
`Registry/Analysis/HiveAnalyzer.cs`, callable from `RegistryToolkit` via
`--analyzeBloat`.

### 1. Cell-record signature/size accounting

First pass: total up in-use vs. free bytes by cell-record type (nk/vk/sk/
lists) to see where the bytes are actually going. This confirmed the size
was overwhelmingly in NK (key) records and their associated small VK
(value) records, not, say, orphaned free-cell slack or a handful of huge
binary values.

### 2. Duplicate-cell-content hashing (`FindDuplicateCellGroups`)

Hypothesis: if the hive were the result of some file-level corruption or a
"race" duplicating hbins/records, we would expect to find many *byte-for-
byte identical* cells. `FindDuplicateCellGroups` hashes (SHA-256) the first
N bytes of every cell's payload for several values of N (4, 8, 12, 16, 32,
64 bytes) and groups cells by that hash.

**Result**: massive duplicate clusters were found — up to ~1,000,000 cells
sharing the same 16-byte-prefix hash. Initially this looked like strong
evidence of corruption/duplication. However, combined with finding #4
below, this turned out to be a **false lead**: these are not literal
duplicate/corrupted records, but distinct VK (value) cells that happen to
share the same value **name** and small fixed-size **data** (e.g. a
`REG_DWORD` value named `IsDiagTypeEvent` set to the same value across
hundreds of thousands of near-identical sibling keys). This is templating,
not corruption — an important caveat now called out explicitly in both the
code's XML docs and the `--analyzeBloat` CLI output, so this doesn't
mislead future investigations.

### 3. Reachability validation (`AnalyzeReachability`)

To rule out actual structural corruption (orphaned cells, broken subkey/
value list pointers, etc.), a live tree-walk was performed starting from
`hive.Root`, recursively following subkey-list and value-list pointers, and
counting how many NK/VK cells are reachable this way. This was compared
against the total number of in-use NK/VK cells found by a raw cell-record
scan (`hive.CellRecords.Values.Count(c => c is NkCellRecord && !c.IsFree)`,
etc.).

**Result**: reachable counts matched the total in-use counts almost
exactly (off by fewer than 300 records out of over a million — well within
normal noise from things like deleted-but-not-yet-reused cells). This is
the key piece of evidence that **the hive's internal structure is sound**:
every NK/VK cell that "exists" is legitimately reachable from the root, in
the expected tree shape. There is no evidence of broken pointers, orphaned
subtrees, or list corruption.

### 4. Subkey/value bloat ranking (`GetTopSubkeyCounts` / `GetTopValueCounts`)

With structural corruption ruled out, the next question was: *which* keys
account for the huge NK count? Ranking every key in the hive by direct
subkey count (and separately, by direct value count) immediately isolated
the culprit:

| Key | Subkey count |
|---|---|
| `HKCU\Software\Microsoft\RdClientRadc\DiagConnectionCache` | **1,014,294** |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs` (and similar) | 300–500 (normal range) |
| `HKCU\...\PushNotifications\Backup` (and similar) | 300–500 (normal range) |

`DiagConnectionCache` alone accounts for **~99% of all NK records** in the
hive (1,014,294 of 1,023,207 total in-use NK cells). Every other key in the
hive is well within normal, unremarkable ranges.

### 5. Subkey naming pattern analysis (`AnalyzeSubkeyNamingPattern`)

Finally, to confirm this was ongoing, "never cleaned up" growth rather than
a one-time bulk-import artifact, the subkey names under
`DiagConnectionCache` were analyzed for structure. All ~1 million subkeys
follow the pattern:

```
{GUID}#N
```

where the GUID identifies a specific RDP connection/session, and `N` is a
simple incrementing counter (per-GUID connection-attempt/diagnostic-record
number). Sampling `LastWriteTimestamp` across many of these subkeys showed
write times spread across many months — e.g. `{094AFE6C-...}#2899`
through `#2908` all written on `2025-08-06`, while `{FE18E419-...}#988`
was written back on `2025-06-17`. Each subkey has a small, consistent set
of ~5 values (`IsDiagTypeEvent`, `ExcludeFromCPL`, `InstallerPinned`,
`InputRedirected`, and similar) — exactly the templated value pattern that
produced the misleading duplicate-hash clusters in step 2.

This confirms the mechanism: every time the RDP client records a
diagnostic/connection event, it creates a **new** subkey rather than
updating/reusing an existing counter slot or pruning old entries — i.e. an
unbounded, ever-growing diagnostic cache. This is consistent with a
known/plausible Windows Remote Desktop Client bug and matches the
originating user's own independent suspicion (formed via manual hexdump
inspection) before this automated analysis was performed.

## Conclusion

* The hive is **not corrupted** — reachability is sound, checksums and
  cell-size/free-flag conventions are all valid, and every byte of size is
  accounted for by legitimate (if excessive) NK/VK records.
* The size is caused almost entirely by one key,
  `Software\Microsoft\RdClientRadc\DiagConnectionCache`, whose subkey count
  grows without bound due to an apparent RDP client defect that never trims
  old diagnostic/connection-cache entries.
* Because a single key has over 1,000,000 subkeys — vastly more than the
  500-entries-per-list convention real hives use (see
  `docs/REGISTRY_HIVE_FORMAT.md` §7) — this hive is also a valuable, if
  extreme, real-world test case for the `ri`-list-chaining logic in
  `RegistrySkeleton`, which was previously untested at this scale.

## Remediation: `--pruneKey` and `--compact`

`RegistryToolkit` now supports remediating exactly this class of problem:

```
# See how much would be pruned without writing anything
RegistryToolkit -f NTUSER.DAT --analyzeBloat

# Prune DiagConnectionCache down to its 500 most-recently-written subkeys,
# writing the result to a new file (the original is never modified in place)
RegistryToolkit -f NTUSER.DAT \
  --pruneKey "Software\Microsoft\RdClientRadc\DiagConnectionCache" \
  --keepRecent 500 \
  -o NTUSER.DAT.pruned

# General compaction (drops free/deleted-cell slack; independent of pruning)
RegistryToolkit -f NTUSER.DAT --compact -o NTUSER.DAT.compacted
```

`--pruneKey` keeps the N most-recently-written subkeys (by
`LastWriteTimestamp`) under the given key and drops the rest entirely —
their NK/VK cells simply do not exist in the output hive — while leaving
every other key in the hive completely untouched. This is implemented via
`Registry.Compaction.HivePruner` (built on top of `RegistrySkeleton`'s
whole-hive reproduction plus a new subtree-exclusion mechanism), and always
writes to a new output file rather than mutating the input.

## Detecting this class of issue in the future

`--analyzeBloat` is meant to be run proactively (or as a first triage step
whenever a hive is unexpectedly large) and will report:

* Reachability stats (a large mismatch would indicate real corruption,
  distinct from this bloat scenario).
* The top keys by subkey/value count.
* If the single largest key's subkeys follow a templated `{prefix}#N`-style
  naming pattern (as `DiagConnectionCache` does), it is flagged explicitly
  with the oldest/newest `LastWriteTimestamp` spread as a strong signal of
  "unbounded application-driven growth" rather than corruption.
* Large duplicate-cell-signature clusters, with an explicit caveat that
  these usually indicate templated sibling-key data (as found here), not
  literal record duplication — to avoid future investigators being misled
  by this the way the duplicate-hash step initially was in this
  investigation.
