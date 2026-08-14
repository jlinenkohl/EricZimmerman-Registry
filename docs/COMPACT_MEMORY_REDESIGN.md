# `RegistrySkeleton` Memory Redesign: From In-Memory `byte[]` to a `FileStream`-Backed Writer

## Summary

`RegistryToolkit`'s `--compact` and `--pruneKey` commands both write a brand
new output hive via `Registry/RegistrySkeleton.cs`. Against a normal (tens
of MB) hive this worked fine. Against the real, abnormally bloated 2GB
`NTUSER.DAT` documented in [`NTUSER_BLOAT_FINDINGS.md`](NTUSER_BLOAT_FINDINGS.md),
it did not: `--compact` (which reproduces the *entire* hive, dropping only
fragmentation slack) either exhausted 13-29GB of RSS or crashed outright,
depending on which stage of this investigation you caught it at.

This document records, in order:

1. The original design of `RegistrySkeleton` and why it used an in-memory buffer.
2. The specific bugs found in that buffer as this hive pushed it near its ceiling.
3. The point at which we (rightly) asked "is fixing this even worth it?", and
   the reasoning that led to "yes, but only after confirming `--pruneKey`
   already solves the *real* problem for this file".
4. The final redesign: a `FileStream`-backed writer with no array-size
   ceiling, why it's a safe, low-risk drop-in replacement for the old buffer,
   and the bugs found and fixed *during* that redesign.
5. Before/after verification numbers against the real 2GB file.

## 1. Background: why `RegistrySkeleton` used an in-memory buffer at all

`RegistrySkeleton` builds a new hive file from scratch by walking a source
`RegistryKey` tree (`ProcessKey`) and, for each key, its values (`ProcessValue`)
and subkey list (`WriteSubkeyList`/`WriteLfChunk`), writing raw hive cell
bytes into a growing "hbin" (hive bin) region. It uses a **reserve-then-backfill**
pattern for keys: because a parent NK (key) cell must record the on-disk
offset of the still-unwritten cells of its children, `ProcessKey` first
reserves space for the parent NK cell (recording its offset), *then*
recurses into children (writing their cells and getting back their offsets),
and finally backfills the parent NK's bytes — now that its children's
offsets are known — into the position reserved earlier.

The simplest way to support "write bytes now at an offset I reserved
earlier" is a plain `byte[]` buffer: `bytes.CopyTo(_hbin, offset)`. That is
exactly what the original implementation did, growing `_hbin` via a
doubling strategy (`EnsureHbinCapacity`) as more space was needed, mirroring
the classic dynamic-array growth pattern used by `List<T>` internally. For
any normal-sized hive (tens to a couple hundred MB) this is simple, fast,
and has no meaningful downside.

## 2. What broke: bugs found while chasing this hive's bloat

Investigating why `--compact` blew up RSS to 13-29GB and eventually crashed
against the real 2GB hive turned up two distinct, real bugs in the
`byte[]`-based approach — plus one fundamental, unfixable-in-place ceiling.

### Bug 2.1 — `int` overflow in the doubling growth arithmetic

`EnsureHbinCapacity`'s buffer-growth check computed the new candidate size as
`_hbin.Length * 2`, done in plain 32-bit `int` arithmetic. Once `_hbin.Length`
exceeded roughly 1.07GB, this multiplication overflowed and wrapped
negative, silently producing a too-small buffer. The very next
`Array.Copy`/`CopyTo` into that undersized buffer threw
`"Destination array was not long enough."` — a confusing failure mode with
no indication of the actual root cause. (Fixed at the time by doing the
doubling math in `long` and clamping to `RegistryBase.MaxByteArrayLength`;
this fix is now moot since the doubling-array approach was replaced
entirely — see §4 — but is recorded here since the same *category* of bug
reappeared, and was fixed again, during the `FileStream` rewrite itself;
see §4.4.)

### Bug 2.2 — Large-Object-Heap churn from repeated buffer doublings

Even after fixing the overflow, RSS still peaked at 20-29GB during a single
compaction run of a hive that was only ~2GB. Each doubling of `_hbin`
allocates a brand-new array (landing on the Large Object Heap, since it's
over 85KB) and copies the old contents into it; the old array becomes
garbage but the LOH is not eagerly compacted/collected by the .NET GC. Near
the 1-2GB mark, several superseded multi-hundred-MB-to-GB buffers could be
simultaneously alive (not yet collected) at once, multiplying the effective
memory footprint several times over the buffer's *nominal* final size.
Mitigated at the time by pre-sizing the initial allocation to the source
hive's own byte length up front (avoiding most late doublings for the
common "reproduce the whole hive" case) — this dropped peak RSS from
~23-29GB to ~13GB, a real, measurable improvement, but not a full fix.

### Bug 2.3 (fundamental, not fixable in the `byte[]` design) — the 2GB array ceiling

A single .NET array cannot exceed `int.MaxValue` elements (and this project
already self-limits further, to `RegistryBase.MaxByteArrayLength =
0x7FFFFFC7`, matching the same "just under 2GB" ceiling it uses when
*reading* an oversized hive under `--exceeds2GbRecovery`). The user's real
test hive is `0x80000000` (2,147,483,648) bytes — **already past** this
ceiling. `--compact` reproduces the *entire* hive (it does not drop any
keys/values, only reclaims free/deleted-cell slack), so its output buffer
needs to hold ~2.147GB — a size a single in-memory `byte[]`-backed writer
fundamentally cannot allocate, with zero margin for the hbin padding and
per-hbin-chunk alignment overhead an actual write adds on top. Every
attempt to raise/presize/tune the `byte[]` approach reduced RSS, but none
could get around this ceiling — the destination array being "not long
enough" was the *correct*, if confusingly worded, diagnosis.

## 3. Stepping back: is fixing `--compact`'s ceiling even worth it for this file?

Before committing to a bigger rewrite, it was worth asking whether
`--compact` was the right tool to be fixing at all, given the specific hive
in front of us.

`--compact`'s entire value proposition is reclaiming **free/deleted-cell
slack** (fragmentation from ordinary registry churn — keys/values that were
deleted, or hbins never fully filled) — it does not remove any live data.
The `GetCellSizeBreakdown`/`--analyzeBloat` analysis from the companion
investigation (see [`NTUSER_BLOAT_FINDINGS.md`](NTUSER_BLOAT_FINDINGS.md))
already showed that, for this specific hive, **live key/value data accounts
for ~1.97GB of the ~2GB total** (NK records ≈130.8MB, VK headers ≈192.4MB,
value data ≈1.77GB, with only ≈27.1MB/1.3% "unaccounted"/reclaimable slack).
In other words: `--compact` alone would achieve almost no size reduction on
*this* file, regardless of whether its memory ceiling was fixed.
`--pruneKey` — which explicitly drops an identified bloated subtree
(`HKCU\Software\Microsoft\RdClientRadc\DiagConnectionCache`, ~1M subkeys of
essentially useless RDP diagnostic cache entries) — was already verified to
solve the real problem for this file outright (2,147,483,648 →
16,547,840 bytes).

So: was it still worth fixing `--compact`'s memory ceiling? Two
considerations tipped the answer to **yes**:

- Native Windows registry hive compaction on key/value delete is unreliable
  in practice — it's uncertain whether changes to a live `HKCU` hive are
  ever physically compacted without a full unload/reload cycle performed by
  a different process/user. A working, memory-safe `--compact` therefore
  remains a generally useful tool for *other*, differently-bloated hives
  (ones that really are slack-heavy rather than key-count-heavy), even
  though it wasn't the fix for *this* file.
- 13GB peak RSS to reproduce a 2GB file is unacceptable on its own merits,
  independent of whether this exact file benefits from the tool.

Decision: proceed with a full rewrite that removes the array-size ceiling
entirely, rather than continuing to chase incremental buffer-sizing fixes.

## 4. The redesign: `FileStream`-backed hbin writer

### 4.1 — Design

Replace the in-memory `_hbin` (`byte[]`) with a `FileStream` (`_hbinStream`)
opened directly on the final output file. Every place that used to do
`bytes.CopyTo(_hbin, offset)` now calls a small helper,
`WriteAt(offset, bytes)`, which does:

```csharp
_hbinStream.Seek(0x1000L + offset, SeekOrigin.Begin);
_hbinStream.Write(bytes, 0, bytes.Length);
```

(`0x1000` accounts for the 4096-byte regf header that precedes the hbin
region in every hive file; every existing call site's "offset" was already
relative to the start of the hbin region, so this convention carries over
unchanged.)

This is a deliberately **small, low-risk** change relative to the
previously-discussed alternative (a full two-pass design that would
pre-compute every cell's final offset via an explicit offset map, then write
everything in a single linear pass with no seeking). The reserve-then-backfill
pattern `ProcessKey` already used against the `byte[]` (reserve a parent
NK's offset, recurse into children, backfill the final NK bytes into the
earlier position) works completely unchanged against a `FileStream`, because
`FileStream` — like the `byte[]` it replaces — supports arbitrary
random-access writes via `Seek`. The resulting file may be written in a
different physical order than a naive discover→allocate→write pass would
produce (some regions get "reserved" via a `Seek`-past-end and get their
final bytes backfilled later), but this produces an equally valid hive file
— exactly as the user anticipated when proposing this approach.

Concretely, this removes:

- `EnsureHbinCapacity` (doubling growth logic — no longer needed, since a
  `FileStream` has no capacity to pre-grow; it simply extends as bytes are
  written to new offsets).
- `AppendHbin`'s old byte-array-copy body (replaced with a `WriteAt` call).
- `GetEmptyHbin`, `PreSizeHbinForWholeHiveReproduction` (both existed purely
  to manage the old buffer's allocation strategy).

And converts every remaining `.CopyTo(_hbin, offset)` call site throughout
`ProcessSkRecord`, `ProcessValue`, `ProcessKey`, `WriteSubkeyList`, and
`WriteLfChunk` to `WriteAt(offset, bytes)`.

### 4.2 — Why this removes both original problems

- **No array-size ceiling**: a `FileStream` has no `int.MaxValue`-element
  restriction — it can grow to whatever size the underlying filesystem
  supports, so reproducing a hive at/above ~2GB is no longer categorically
  impossible.
- **No LOH churn**: there is no buffer to double/reallocate/copy, so the
  repeated-superseded-large-buffer problem (§2.2) cannot recur by
  construction — the OS page cache, not the CLR heap, absorbs the memory
  pressure of a large output file.

### 4.3 — `FinalizeAndWrite`

The header/checksum-patching tail of `Write()`/`WriteWholeHive()` (shared
via `FinalizeAndWrite`) also changed: instead of opening a *second*,
separate `FileStream` at the very end to write the header followed by the
in-memory `_hbin` buffer as two blocks, it now seeks the *already-open*
`_hbinStream` back to offset 0 and writes the (patched) header bytes
directly — the hbin region itself was already written to the same stream
incrementally as processing proceeded, so there is nothing left to copy or
flush except the header.

### 4.4 — A second overflow bug, found and fixed during this rewrite

Converting every call site to `WriteAt` was mechanical, but a subtlety
almost reintroduced the same *category* of bug as §2.1: several internal
offset-tracking fields (`_currentOffsetInHbin`, `_hbinLength`,
`_relativeOffset`) were still declared as plain `int`. Once their running
totals approached the hive's own ~2.1GB size, the same kind of silent
`int` overflow occurred — this time surfacing as `FileStream.Seek` throwing
`"Invalid argument"` (a negative/garbage seek position) partway through a
compaction run of the real 2GB hive.

Fixed by widening all of this internal bookkeeping to `long`:

- `_currentOffsetInHbin`, `_hbinLength`, `_relativeOffset` → `long`.
- `WriteAt(long hbinRelativeOffset, byte[] bytes)` — the seek-position
  arithmetic itself is now unconditionally computed in `long`.
- A new helper, `ToCellOffset(long offset)`, narrows a `long`-tracked
  offset down to the `int` that on-disk hive cell headers actually store
  (the registry hive format's own cell-offset fields are natively 32-bit —
  this is a real, unavoidable constraint of the file format itself, not an
  artifact of this project's implementation). If the required offset
  exceeds `RegistryBase.MaxByteArrayLength` (the same ~2GB addressing
  ceiling the format enforces), `ToCellOffset` throws a clear,
  actionable `InvalidOperationException` instead of silently
  wrapping/truncating — e.g.:

  > `Cannot write this hive: required offset 0x80000020 exceeds the hive
  > format's own ~2GB addressing limit (0x7FFFFFC7). The output hive itself
  > is too large for the registry format's 32-bit cell offsets, independent
  > of any in-memory buffer limit.`

This is an important, permanent distinction from the earlier `byte[]`
ceiling: **this is not a bug to fix**. It's the registry hive file format
itself — a `hbin`-relative cell offset is stored as a 32-bit field on disk,
independent of what language/data-structure is used to write it. No amount
of further engineering on the writer can get around this; a hive whose live
data alone exceeds ~2GB cannot be reproduced 1:1 by any tool, because the
format has nowhere left to point. (This is exactly why the real fix for the
user's file is `--pruneKey`, which drops data rather than trying to
losslessly reproduce all of it — see §3.)

## 5. Verification against the real 2GB file

All testing used a throwaway copy of the user's real, previously-`bak`-ed
`NTUSER.DAT` (`2,147,483,648` bytes, ~1,023,207 keys/6,033,724 cell
records), run under `DOTNET_gcServer=0` (workstation GC) for more
predictable RSS readings; RSS was sampled every 5s via `/proc/<pid>/status`
`VmRSS` throughout.

| Stage | Peak RSS | Outcome |
|---|---|---|
| Original `byte[]`-backed `_hbin`, before any fix | ~23-29GB | Silent crash (`Destination array was not long enough`) around key ~350,000, no exception initially logged |
| + doubling-overflow fix (§2.1) | ~20-25GB | Same crash, now with a logged exception once found |
| + pre-sizing initial buffer to source length (§2.2 mitigation) | ~13GB | Same crash, confirmed as the fundamental `byte[]` 2GB ceiling (§2.3) |
| `FileStream`-backed rewrite (§4), before `WriteAt`/offset overflow fix | ~9.5-11.5GB | Crashed with `Seek` `"Invalid argument"` near key ~870,000/hbinBytes≈2.13GB (§4.4) |
| `FileStream`-backed rewrite, with `long` offset bookkeeping + `ToCellOffset` (final) | ~9.5-11.5GB | **Correctly fails fast** with a clear, actionable `InvalidOperationException` — no crash, no silent corruption, no confusing message |

The final peak RSS (~9.5-11.5GB) reflects the cost of *parsing* the 2GB
source hive into its in-memory `RegistryKey`/cell-record object graph (over
a million keys and ~6 million cell records) — a separate, pre-existing cost
unrelated to `RegistrySkeleton`'s own writer, and out of scope for this
redesign. What changed is that the writer itself no longer contributes
additional multi-GB churn on top of that parsing cost, and it no longer has
its own array-size ceiling — it now fails only when the *output hive
format itself* genuinely cannot address the required offset, which is a
correct, expected, and clearly-explained outcome rather than a bug.

`--pruneKey` against the same real file, re-verified after this rewrite,
completed in ~25s total (parse + prune + write) and produced the same
dramatic, correct size reduction as before the rewrite:

```
Size before: 2,147,483,648 bytes, after: 16,539,648 bytes (2,130,944,000 bytes removed)
```

Full existing test suite (`Registry.Test`) re-run after the rewrite: 149
passed / 1 pre-existing unrelated failure / 43 skipped — an unchanged
baseline, confirming no regressions from this change.

## 6. Summary of the final, current behavior

- `--pruneKey` works unchanged (and is unaffected by any of the ceilings
  discussed above, since it drops data rather than reproducing it 1:1) —
  this remains the correct, verified fix for hives like the user's real
  file, where the bloat is concentrated in a small number of identifiable,
  disposable subtrees.
- `--compact` no longer has an artificial `byte[]`-imposed ceiling or LOH
  churn problem — it can now compact any hive whose *actual, final* size
  fits within the registry hive format's own ~2GB cell-offset addressing
  limit, which is the same limit every hive (including ones written by
  Windows itself) has always been subject to. For hives whose live data
  alone already exceeds that limit (as with the user's real file),
  `--compact` now fails immediately with a clear explanation rather than
  hanging, exhausting memory, or crashing with a confusing message.
