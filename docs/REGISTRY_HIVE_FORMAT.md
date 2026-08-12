# Windows Registry Hive File Format (as implemented in this codebase)

This document summarizes the on-disk layout of a Windows registry hive file
(`regf` format) as understood and relied upon by this codebase — particularly
by `Registry/RegistryBase.cs` (parsing), `Registry/RegistrySkeleton.cs`
(rewriting/compaction), and `Registry/Analysis/HiveAnalyzer.cs` (bloat
analysis). It is written so that anyone extending the parser, the skeleton
writer, or the analyzer/pruner tools has a single, human-readable reference
for "what has to stay consistent" when a hive is read or rewritten.

This is not a full formal specification (see the References section for
those); it documents the specific fields and invariants this project's code
depends on.

## 1. Overall file layout

```
Offset 0x0000              regf header (4096 bytes / 0x1000)
Offset 0x1000              hbin #0
Offset 0x1000 + hbin0.size hbin #1
...                        additional hbins, back-to-back, until EOF
```

* The header always occupies exactly the first `0x1000` (4096) bytes of the
  file.
* Everything after the header is a sequence of **hbin** ("hive bin")
  containers. Each hbin is itself a sequence of variable-length **cells**.
* There is no index of hbins; hbins are located by walking the file
  sequentially from `0x1000`, using each hbin's own declared size to find the
  next one.

## 2. The `regf` header (offset 0x0)

Key fields (all little-endian):

| Offset  | Field                     | Notes |
|---------|---------------------------|-------|
| 0x00    | Signature                 | ASCII `regf` |
| 0x04    | PrimarySequenceNumber     | Incremented on each write; must equal SecondarySequenceNumber for a "clean" hive |
| 0x08    | SecondarySequenceNumber   | See above — mismatch means the hive is "dirty" and has pending transaction-log data (`.LOG1`/`.LOG2`) that should be replayed |
| 0x0C    | LastWriteTimestamp        | FILETIME |
| 0x14    | MajorVersion              | Normally 1 |
| 0x18    | MinorVersion              | Normally 3-6 (this codebase writes `5` when rebuilding a header — see `RegistrySkeleton.cs:214`) |
| 0x24    | RootCellOffset            | Relative offset (from `0x1000`, i.e. from the start of hbin data) of the root key's NK cell. Referred to as `RootCellIndex` in this codebase. |
| 0x28    | HiveBinsDataSize           | Total size in bytes of all hbins (i.e. file length minus the 4096-byte header, when the file is not truncated/oversized) |
| 0x30    | FileName                  | Embedded original hive file name/path (UTF-16LE) |
| 0x1FC   | Checksum                  | XOR checksum — see below |

**Header checksum algorithm** (see `RegistrySkeleton.Write()` and
`RegistryHive`/`RegistryHeader` checksum validation): the checksum is the
XOR of every 4-byte little-endian word from offset `0x000` through `0x1FB`
inclusive (i.e. the first 508 32-bit words of the header, NOT including the
checksum field itself at `0x1FC`). Any code that rewrites header fields
**must** recompute and rewrite this checksum, or tools that validate hive
integrity (including this project's own `--integrity` mode) will flag the
hive as corrupt.

**Dirty-hive / transaction log relationship**: `PrimarySequenceNumber !=
SecondarySequenceNumber` indicates the hive was not cleanly unmounted and
that adjacent `.LOG1`/`.LOG2` transaction log files (if present) contain
dirty-page data that must be replayed to reconstruct the true, current state
of the hive. This project's `TransactionLog` class and
`RegistryHive.ProcessTransactionLogs()` implement this replay. When
rewriting/compacting a hive from scratch (as `RegistrySkeleton` does), the
output hive is always written as a fresh, clean hive (sequence numbers
matched, no pending log data), which is why compaction tools should replay
any existing transaction logs on the *input* side first, before compacting.

## 3. hbin ("hive bin") structure

Each hbin begins with a fixed 32-byte header:

| Offset | Field              | Notes |
|--------|--------------------|-------|
| 0x00   | Signature          | ASCII `hbin` (bytes `68 62 69 6E`) |
| 0x04   | RelativeOffset     | This hbin's own offset, relative to the start of hbin data (`0x1000`) — i.e. hbin N's RelativeOffset equals the sum of the sizes of hbins 0..N-1 |
| 0x08   | Size               | Total size of this hbin in bytes, including this 32-byte header. Always a multiple of `0x1000` (4096) |
| 0x14   | LastWriteTimestamp | FILETIME (only meaningful/set on the first hbin in some implementations) |

Everything from offset `0x20` (32) to `Size` within the hbin is a sequence of
**cells** (see below), packed back-to-back with no padding other than each
cell's own size rounding.

**hbin size invariant**: `Size` must always be large enough to hold every
cell written into it, and in practice is always a multiple of 4096 bytes.
`RegistrySkeleton` tracks a "current hbin" write cursor and grows
(allocates a new hbin) via `CheckhbinSize()` whenever the next cell to write
would not fit in the remaining space of the current hbin.

## 4. Cells: the size-prefix / free-flag convention

Every record in a hive — NK, VK, SK, and list records (`lf`/`lh`/`ri`/`li`)
alike — is stored inside a **cell**, and every cell begins with a 4-byte
signed `Int32` **size** field:

* **Negative size** → the cell is **in use**. The magnitude (absolute value)
  of the size is the cell's total length in bytes (including this 4-byte
  size prefix).
* **Positive size** → the cell is **free** (deleted/unallocated slack). The
  value is the size of the free region, again including the prefix.

This convention is implemented in `NkCellRecord.IsFree` (and the equivalent
properties on `VkCellRecord`/`SkCellRecord`/list records):
`IsFree => BitConverter.ToInt32(RawBytes, 0) > 0`.

Any code that writes cells (e.g. `RegistrySkeleton`) must always write cell
sizes as **negative** (in-use) — see the repeated
`BitConverter.GetBytes(-1 * xBytes.Length).CopyTo(xBytes, 0)` pattern
throughout `RegistrySkeleton.cs` — otherwise other tools (including
Microsoft's own APIs) will treat the record as free/garbage and ignore it,
even though the bytes are otherwise well-formed.

Cell content immediately follows the 4-byte size prefix; the next cell
begins immediately after this cell's declared (absolute) length. Cell
lengths are always a multiple of 8 bytes (padded as needed), a convention
this codebase's writer follows implicitly through its buffer-sizing
arithmetic.

## 5. NK (Key Node) records

Signature: ASCII `nk` at relative offset `0x4` within the cell (i.e. right
after the 4-byte cell size prefix).

Field offsets used by this codebase (offsets are relative to the start of
the cell, i.e. *including* the 4-byte size prefix at `0x0`):

| Offset | Field                        | Constant name (RegistrySkeleton.cs) |
|--------|------------------------------|--------------------------------------|
| 0x14   | ParentCellIndex              | `ParentCellIndex` — relative offset of parent NK cell |
| 0x18   | SubkeyCountStable             | `SubkeyCountStableOffset` — number of *stable* (non-volatile) subkeys |
| 0x20   | SubkeyListsStableCellIndex    | `SubkeyListsStableCellIndex` — relative offset of this key's subkey list (`lf`/`lh`, or `ri` if chained — see §7) |
| 0x28   | ValueCount                   | `ValueCountIndex` — number of values directly under this key |
| 0x2C   | ValueListCellIndex           | `ValueListCellIndex` — relative offset of this key's value-pointer list (a flat array of VK cell offsets) |
| 0x30   | SecurityCellIndex            | `SecurityOffset` — relative offset of this key's SK (security descriptor) cell |
| 0x34   | ClassCellIndex               | `ClassOffset` — relative offset of this key's class-name cell (rare; typically unused/absent) |

Additional NK header fields relied upon elsewhere in the codebase (not
listed as named constants in `RegistrySkeleton.cs`, but read by the parser):
key-name-length, class-name-length, and (for the *root* NK only) special
flags indicating it is the hive's root key. `LastWriteTimestamp` (a FILETIME)
is present in every NK header and is what
`Registry.Abstractions.RegistryKey.LastWriteTime` surfaces — this is the
field used throughout the bloat-analysis and pruning tooling (§8/§9) to
decide "most recently written" vs. "stale" subkeys.

**Invariant**: whenever a key's set of subkeys changes (e.g. during pruning
or compaction), both `SubkeyCountStable` (0x18) and
`SubkeyListsStableCellIndex` (0x20) must be updated together and kept
consistent with the actual subkey list cell written for this key — a
mismatch here is exactly the kind of "off by a small amount" inconsistency
that `HiveAnalyzer.AnalyzeReachability` is designed to detect (see §9).

## 6. VK (Value) records

Signature: ASCII `vk`. Key field used by this codebase:

| Offset | Field            | Constant name |
|--------|------------------|---------------|
| 0x0C   | ValueDataOffset  | `ValueDataOffset` — for values whose data is small enough to fit in this 4-byte pointer field, it *is* the raw data (in-line storage, "data inline" flag set in the length field); otherwise it is the relative offset of a separate cell (or a `db` big-data record — see §7) holding the actual value bytes |

A key's values are not referenced individually from the NK record; instead,
the NK's `ValueListCellIndex` (§5) points to a flat array cell containing
`ValueCount` 4-byte relative offsets, each pointing to one VK cell.

## 7. Subkey lists: `lf` / `lh` / `ri` / `li`, and the 500-entry chaining rule

A key's subkeys are **not** referenced directly from the NK record's
`SubkeyListsStableCellIndex` as a flat array of NK offsets in all cases —
instead, that field points to one of these list-record types (identified by
a 2-byte ASCII signature at relative offset `0x4` within the list's cell,
immediately after the list cell's own 4-byte size prefix):

* **`lf`** — "fast leaf": entries are `(4-byte NK cell offset, 4-byte hash
  hint)` pairs. The hash hint is the first 4 bytes of the subkey's name,
  used by native Windows tools to speed up name comparisons; this codebase
  writes it (see `RegistrySkeleton.BuildlfList`) but does not rely on it for
  correctness during parsing.
* **`lh`** — like `lf`, but the 4-byte hint is a proper hash of the subkey
  name rather than raw name bytes. Functionally interchangeable with `lf`
  for this codebase's purposes (both are simple flat lists of subkey
  offsets with a hint).
* **`li`** — a flat list of NK cell offsets with **no** hint field at all
  (just `4-byte count` + N × `4-byte offset`). Used in older / smaller
  hives.
* **`ri`** — "root index": used to **chain together multiple `lf`/`lh`
  lists** when a single key has more subkeys than fit comfortably in one
  list. An `ri` record's entries are 4-byte offsets, each pointing to
  another `lf`/`lh` (or, in principle, nested `ri`) list record, rather than
  directly to NK cells.

**Why chaining matters (and the historical gap this project had)**: real
Windows hives cap each `lf`/`lh` list at **500 entries**; a key with more
than 500 subkeys stores its subkeys across multiple `lf`/`lh` lists of up to
500 entries each, chained together via a single `ri` list pointed to by the
NK's `SubkeyListsStableCellIndex`. Prior to this project's pruning/
compaction work, `RegistrySkeleton` only ever built a single flat `lf` list
regardless of subkey count — silently producing a hive that other tools
(and even this project's own parser, depending on path) could mis-read for
any key with more than 500 subkeys. This is exactly the scenario the
`DiagConnectionCache` investigation (see
`docs/NTUSER_BLOAT_FINDINGS.md`) surfaced, since that key alone has over
one million subkeys. `RegistrySkeleton.WriteSubkeyList` (added alongside the
pruning feature) now chunks subkey lists into ≤500-entry `lf` lists and
chains them with an `ri` list whenever a key's (retained) subkey count
exceeds 500.

## 8. `db` (big data) records

When a value's data exceeds roughly 16KB, it is not stored contiguously;
instead a `db` cell holds a small header plus a list of offsets to
fixed-size data-segment cells (commonly 16344-byte chunks), which are
concatenated on read to reconstruct the full value. This codebase's
`DbListRecord`/`BigDataBlock` classes implement this; it is mentioned here
for completeness since it is another place where "cell offsets pointing to
other cells" is the load-bearing invariant, similar to `ri`→`lf` chaining.

## 9. Cross-cutting invariants relied on by the analysis/pruning tools

* **Reachability**: starting from the header's `RootCellOffset` and
  recursively following NK → (subkey list → NK)\* and NK → (value list →
  VK)\* pointers should visit (very close to) every in-use NK/VK cell in the
  hive. `HiveAnalyzer.AnalyzeReachability` performs exactly this walk and
  compares the reachable count against the total in-use cell count; a large
  gap indicates orphaned/corrupted cells, while a near-exact match (as found
  in the real-world 2GB `NTUSER.DAT` case) proves the hive is structurally
  sound even if unusually large.
* **Checksum consistency**: any header field mutation requires recomputing
  the `0x1FC` checksum (see §2).
- **Offset consistency**: any change to a key's subkeys or values requires
  updating both the *count* field and the *pointer* field together (§5), and
  — if the count crosses the 500-entry threshold in either direction —
  potentially restructuring between a flat `lf`/`lh` list and an `ri`-chained
  set of lists (§7).

## References

For a more exhaustive, byte-by-byte specification beyond what this project's
code depends on, see:

* Joachim Metz's forensic wiki: <https://github.com/libyal/libregf/blob/main/documentation/Windows%20NT%20Registry%20File%20(REGF)%20format.asciidoc>
* Eric Zimmerman's own registry research/tools, which this codebase's
  parser and skeleton writer were originally derived from.
