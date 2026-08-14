# RegistryToolkit

## What is this?

`RegistryToolkit` is a CLI utility (in this fork, built on top of the
`Registry` parsing library) for analyzing, validating, and repairing
Windows Registry hive files — with a particular focus on hives that are
**abnormally large, truncated, or corrupted** in ways the original
`ExampleApp` (the upstream project's minimal demo/starter app) was never
designed to handle.

It started life as an experimental analyzer/fixer mode bolted onto
`ExampleApp`, but was graduated into its own standalone project once it
became clear this was a real, ongoing utility rather than a one-off
demo. `ExampleApp` itself has been reverted back to its original upstream
(`origin/master`) state and is untouched by any of this work.

## Why we needed it

This all started from a concrete, real-world problem: a `NTUSER.DAT` hive
had grown to **exactly 2,147,483,648 bytes (2GB)** — large enough to:

1. Trip the .NET single-array 2GB allocation ceiling that the original
   parser relied on, making the hive **fail to load at all** ("Array
   dimensions exceeded supported range").
2. Once loadable, raise the obvious follow-up question: *is this hive
   actually corrupted, or is it just... genuinely this big?* (Typical
   `NTUSER.DAT` hives are tens of MB; a heavily-used one might reach a
   couple hundred MB. 2GB is 10-100x larger than that.)
3. Once the answer turned out to be "no data corruption, but ~1 million
   bloated subkeys from a single misbehaving application" — raise the
   next question: how do you actually *fix* a hive like this, safely,
   without exhausting tens of gigabytes of RAM in the process?

Each of these turned into real engineering work, all captured in the docs
below.

## What it solves

| Problem | Solution |
|---|---|
| Hive file size ≥ 2GB fails to load (`Array dimensions exceeded supported range`) | `--exceeds2GbRecovery`: caps reads to the CLR's actual byte-array limit and loads as much of the hive as is addressable, instead of crashing outright |
| Silent/confusing crashes on truncated, padded, or otherwise malformed hives | `--integrity` (+ `--integrityLog`): best-effort parsing that surfaces and continues past supported corruption conditions instead of throwing, and mirrors the full console transcript to a log file |
| "Is this abnormally large hive corrupted, or just legitimately huge?" | `--analyzeBloat`: reports cell-size breakdowns (NK/VK/value-data byte totals), top offenders by both key **count** and total **byte size**, and prints a ready-to-run suggested `--pruneKey` command for the worst offender |
| A single runaway key (e.g. an app's unbounded cache) accounts for the vast majority of a hive's size | `--pruneKey`: rewrites the hive, keeping only the N most-recently-written subkeys under a target key path and dropping the rest — verified to shrink a real 2GB hive down to ~16.5MB |
| General fragmentation/slack from ordinary registry churn (deleted keys/values, partially-filled hbins) | `--compact`: rewrites the entire hive 1:1 while dropping free/deleted-cell slack (does not remove any live data — see [`COMPACT_MEMORY_REDESIGN.md`](COMPACT_MEMORY_REDESIGN.md) for when this is/isn't useful vs. `--pruneKey`) |
| Rewriting multi-GB hives with `--compact`/`--pruneKey` exhausting 13-29GB of RAM (or crashing outright near the 2GB boundary) | A `FileStream`-backed hive writer (replacing an in-memory `byte[]` buffer) with no array-size ceiling and no Large-Object-Heap churn — see [`COMPACT_MEMORY_REDESIGN.md`](COMPACT_MEMORY_REDESIGN.md) |

## Quick usage examples

All examples assume you've built the project (`dotnet build -c Release`)
and are running the resulting `RegistryToolkit` executable/DLL.

**Load and validate a normal hive:**
```
RegistryToolkit -f "C:\path\to\NTUSER.DAT"
```

**Load an oversized (≥2GB) or truncated hive that would otherwise fail to parse:**
```
RegistryToolkit -f "C:\path\to\NTUSER.DAT" --exceeds2GbRecovery
```

**Run best-effort integrity parsing, logging everything to a file for later review:**
```
RegistryToolkit -f "C:\path\to\NTUSER.DAT" --integrity --integrityLog integrity.log
```

**Analyze a suspiciously large hive for bloat (top offenders by count and by size):**
```
RegistryToolkit -f "C:\path\to\NTUSER.DAT" --exceeds2GbRecovery --analyzeBloat
```
This prints a cell-size breakdown and, if it identifies a clear top
offender, a ready-to-run suggested command, e.g.:
```
RegistryToolkit -f "NTUSER.DAT" --pruneKey "Software\Microsoft\RdClientRadc\DiagConnectionCache" --keepRecent 500 -o "NTUSER.pruned.DAT"
```

**Prune a known-bloated key, keeping only its 500 most-recently-written subkeys:**
```
RegistryToolkit -f "C:\path\to\NTUSER.DAT" --exceeds2GbRecovery --pruneKey "Software\Microsoft\RdClientRadc\DiagConnectionCache" --keepRecent 500 -o "NTUSER.pruned.DAT"
```

**Compact a hive (reclaim free/deleted-cell slack, keep all live data):**
```
RegistryToolkit -f "C:\path\to\NTUSER.DAT" --compact -o "NTUSER.compact.DAT"
```

Run `RegistryToolkit --help` for the full option reference.

## Other improvements made along the way

Beyond the headline `--analyzeBloat`/`--pruneKey`/`--compact` features
above, this fork also includes a number of underlying correctness fixes to
the `Registry` parsing library itself, all driven by real failures
encountered while working with the oversized hive described above:

- Fixed `Array dimensions exceeded supported range` for hives ≥2GB by
  capping byte-array reads to the CLR's actual maximum array length
  instead of attempting to allocate a same-size-as-file buffer outright.
- Fixed silent `TotalBytesRead` overshoot and a false-alarm
  trailing-data-mismatch warning that could otherwise obscure real
  corruption signals.
- Fixed a stream-length overflow bug affecting hives ≥2GB (handling
  `long` stream lengths correctly instead of truncating/wrapping).
- Fixed hangs/severe slowdowns when parsing or writing hives with 1M+
  keys (see [`COMPACT_MEMORY_REDESIGN.md`](COMPACT_MEMORY_REDESIGN.md) and
  the git history for the specific bottlenecks found and fixed).
- Enabled and cross-platform-fixed the existing test suite, and added new
  regression tests specifically covering ≥2GB hive handling and the new
  analyzer/compaction/pruning features.
- Fixed a real memory-configuration bug in the shipped `RegistryToolkit`
  build itself (Server GC vs. Workstation GC) that was inflating peak RSS
  from ~11GB to ~17GB on multi-core machines for no throughput benefit —
  see [`COMPACT_MEMORY_REDESIGN.md`](COMPACT_MEMORY_REDESIGN.md) §7.

## Further reading

- [`REGISTRY_HIVE_FORMAT.md`](REGISTRY_HIVE_FORMAT.md) — a from-scratch,
  human-friendly writeup of the on-disk registry hive format (cell
  structure, offsets, checksums, hbin layout, etc.), written from direct
  hexdump/code investigation while building the analyzer and writer.
- [`NTUSER_BLOAT_FINDINGS.md`](NTUSER_BLOAT_FINDINGS.md) — the case study
  of the real 2GB hive that motivated all of this work: how it was
  diagnosed, what was actually wrong (an RDP client's unbounded
  diagnostic cache), and how `--analyzeBloat`/`--pruneKey` were used to
  confirm and fix it.
- [`COMPACT_MEMORY_REDESIGN.md`](COMPACT_MEMORY_REDESIGN.md) — the full
  history of making `--compact`/`--pruneKey` memory-safe against
  multi-gigabyte hives: the bugs found, the design decisions made (and
  why), and before/after verification numbers.
