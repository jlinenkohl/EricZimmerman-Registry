using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Registry.Abstractions;
using Registry.Cells;
using Registry.Lists;
using Serilog;

namespace Registry;

public class RegistrySkeleton
{
    private const int SecurityOffset = 0x30;
    private const int ClassOffset = 0x34;
    private const int SubkeyCountStableOffset = 0x18;
    private const int SubkeyListsStableCellIndex = 0x20;
    private const int ValueListCellIndex = 0x2C;
    private const int ValueCountIndex = 0x28;
    private const int ParentCellIndex = 0x14;
    private const int RootCellIndex = 0x24;
    private const int ValueDataOffset = 0x0C;
    private const int HeaderMinorVersion = 0x18;
    private const int CheckSumOffset = 0x1fc;

    private readonly RegistryHive _hive;

    private readonly List<SkeletonKeyRoot> _keys;

    private readonly HashSet<string> _excludedKeyPaths = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<long, int> _skMap = new();

    private int _currentOffsetInHbin = 0x20;

    // _hbin is a growable backing buffer. Its allocated capacity can exceed the logical, in-use hbin size
    // (_hbinLength) -- growth uses doubling instead of reallocating+copying the entire buffer on every
    // single hbin append, which previously made writing large hives (e.g. rewriting a hive with 1M+ NK/VK
    // records) an O(n^2) operation as the buffer grew one hbin chunk (a few KB) at a time.
    private byte[] _hbin = new byte[0];
    private int _hbinLength;

    private int _relativeOffset;

    private long _keysWritten;
    private const int WriteProgressLogIntervalKeys = 10_000;
    private Stopwatch _writeProgressStopwatch;

    // Memoizes ProcessKey() results by key path so that redundant re-visits of the same key (an inherent
    // side effect of ProcessSkeletonTree()'s tree recursion combined with ProcessKey()'s own SubKeys
    // recursion -- see comment in ProcessKey()) are O(1) lookups instead of full re-writes.
    private readonly Dictionary<string, int> _processedKeyOffsets = new(StringComparer.OrdinalIgnoreCase);

    public RegistrySkeleton(RegistryHive hive)
    {
        if (hive == null) throw new NullReferenceException();

        _hive = hive;
        _keys = new List<SkeletonKeyRoot>();
    }

    public ReadOnlyCollection<SkeletonKeyRoot> Keys => _keys.AsReadOnly();

    /// <summary>
    ///     Adds a SkeletonKey to the SkeletonHive
    /// </summary>
    /// <remarks>Returns true if key is already in list or it is added</remarks>
    /// <param name="key"></param>
    /// <returns></returns>
    public bool AddEntry(SkeletonKeyRoot key)
    {
        var hiveKey = _hive.GetKey(key.KeyPath);

        if (hiveKey == null) return false;

        if (key.KeyPath.StartsWith(_hive.Root.KeyName) == false)
        {
            var newKeyPath = $"{_hive.Root.KeyName}\\{key.KeyPath}";
            var tempKey = new SkeletonKeyRoot(newKeyPath, key.AddValues, key.Recursive);
            key = tempKey;
        }

        var intKey = _keys.SingleOrDefault(t => t.KeyPath == key.KeyPath);

        if (intKey == null)
        {
            _keys.Add(key);

            if (key.Recursive)
            {
                // for each subkey in hivekey, create another skr and add it
                var subs = GetSubkeyNames(hiveKey);

                foreach (var sub in subs)
                {
                    var subsk = new SkeletonKeyRoot(sub, true, false);
                    _keys.Add(subsk);
                }
            }
        }

        return true;
    }

    private List<string> GetSubkeyNames(RegistryKey key)
    {
        var l = new List<string>();

        foreach (var registryKey in key.SubKeys)
        {
            l.AddRange(GetSubkeyNames(registryKey));

            l.Add(registryKey.KeyPath);
        }

        return l;
    }

    public bool RemoveEntry(SkeletonKeyRoot key)
    {
        if (key.KeyPath.StartsWith(_hive.Root.KeyName) == false)
        {
            var newKeyPath = $"{_hive.Root.KeyName}\\{key.KeyPath}";
            var tempKey = new SkeletonKeyRoot(newKeyPath, key.AddValues, key.Recursive);
            key = tempKey;
        }

        var intKey = _keys.SingleOrDefault(t => t.KeyPath == key.KeyPath);

        if (intKey == null) return false;

        _keys.Remove(intKey);

        return true;
    }

    /// <summary>
    ///     Removes every previously-added entry whose KeyPath equals <paramref name="keyPath" /> or is nested
    ///     beneath it (i.e. KeyPath == keyPath or KeyPath starts with "keyPath\"), in a single O(n) pass.
    ///     <remarks>
    ///         Intended for bulk pruning of large subtrees (e.g. hundreds of thousands of subkeys), where
    ///         repeatedly calling <see cref="RemoveEntry" /> one entry at a time would be O(n^2) since it
    ///         performs a linear scan per call.
    ///     </remarks>
    /// </summary>
    /// <param name="keyPath">The key path (subtree root) to remove, along with everything nested beneath it.</param>
    /// <returns>The number of entries removed.</returns>
    public int RemoveEntrySubtree(string keyPath)
    {
        if (keyPath.StartsWith(_hive.Root.KeyName, StringComparison.OrdinalIgnoreCase) == false)
        {
            keyPath = $"{_hive.Root.KeyName}\\{keyPath}";
        }

        var prefix = keyPath + "\\";

        return _keys.RemoveAll(k =>
            string.Equals(k.KeyPath, keyPath, StringComparison.OrdinalIgnoreCase) ||
            k.KeyPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Excludes the given key path (and, implicitly, everything nested beneath it, since ProcessKey never
    ///     descends into an excluded key) from the output hive produced by <see cref="Write" />.
    ///     <remarks>
    ///         Unlike <see cref="RemoveEntrySubtree" />, which only removes entries this skeleton was told to
    ///         explicitly add, this affects the *default* recursive copy behavior: when reproducing an entire
    ///         hive (or a large subtree) via <see cref="AddEntry" /> with <c>Recursive = true</c>, excluded
    ///         key paths are skipped entirely rather than being copied into the output.
    ///     </remarks>
    /// </summary>
    /// <param name="keyPath">The key path (subtree root) to exclude when writing the output hive.</param>
    public void ExcludeSubtree(string keyPath)
    {
        if (keyPath.StartsWith(_hive.Root.KeyName, StringComparison.OrdinalIgnoreCase) == false)
        {
            keyPath = $"{_hive.Root.KeyName}\\{keyPath}";
        }

        _excludedKeyPaths.Add(keyPath);
    }

    private void EnsureHbinCapacity(int additionalSize)
    {
        var required = _hbinLength + additionalSize;
        if (required <= _hbin.Length) return;

        // Double the capacity (or grow to exactly what's required if that's larger) rather than growing by
        // exactly the requested amount each time. This amortizes the cost of the underlying array
        // reallocation+copy across many appends instead of paying it on every single hbin chunk added,
        // which is what made writing large hives extremely slow (see field comment on _hbin).
        var newCapacity = Math.Max(_hbin.Length * 2, required);
        if (newCapacity < 4096) newCapacity = 4096;

        var newBuffer = new byte[newCapacity];
        Array.Copy(_hbin, newBuffer, _hbinLength);
        _hbin = newBuffer;
    }

    /// <summary>
    ///     Appends a freshly-created, empty hbin chunk of <paramref name="size" /> bytes to the logical end
    ///     of the output buffer, growing the backing array's capacity as needed (see
    ///     <see cref="EnsureHbinCapacity" />).
    /// </summary>
    private void AppendHbin(int size)
    {
        EnsureHbinCapacity(size);
        GetEmptyHbin(size).CopyTo(_hbin, _hbinLength);
        _hbinLength += size;
    }

    private byte[] GetEmptyHbin(int size)
    {
        var newHbin = new byte[size];

        //signature 'hbin'
        newHbin[0] = 0x68;
        newHbin[1] = 0x62;
        newHbin[2] = 0x69;
        newHbin[3] = 0x6E;

        BitConverter.GetBytes(_relativeOffset).CopyTo(newHbin, 0x4);
        _relativeOffset += size;

        BitConverter.GetBytes(size).CopyTo(newHbin, 0x8); //size

        BitConverter.GetBytes(DateTimeOffset.UtcNow.ToFileTime()).CopyTo(newHbin, 0x14); //last write

        return newHbin;
    }

    public bool Write(string outHive)
    {
        if (_keys.Count == 0)
            throw new InvalidOperationException("At least one SkeletonKey must be added before calling Write");

        if (File.Exists(outHive)) File.Delete(outHive);

        AppendHbin(0x1000);


        var treeKey = BuildKeyTree();

        var parentOffset = ProcessSkeletonTree(treeKey); //always include keys/values for now

        //mark any remaining hbin as free
        var freeSize = _hbinLength - _currentOffsetInHbin;
        if (freeSize > 0) BitConverter.GetBytes(freeSize).CopyTo(_hbin, _currentOffsetInHbin);

        //work is done, get header, update rootcelloffset, adjust its length to match new hbin length, and write it out

        var headerBytes = _hive.ReadBytesFromHive(0, 0x1000);

        BitConverter.GetBytes(_hbinLength).CopyTo(headerBytes, 0x28);
        BitConverter.GetBytes(5).CopyTo(headerBytes, HeaderMinorVersion);
        BitConverter.GetBytes(parentOffset).CopyTo(headerBytes, RootCellIndex);

        //update checksum
        var index = 0;
        var xsum = 0;
        while (index <= 0x1fb)
        {
            xsum ^= BitConverter.ToInt32(headerBytes, index);
            index += 0x04;
        }

        var newcs = xsum;

        BitConverter.GetBytes(newcs).CopyTo(headerBytes, CheckSumOffset);

        using (var fs = new FileStream(outHive, FileMode.Create, FileAccess.Write))
        {
            fs.Write(headerBytes, 0, headerBytes.Length);
            fs.Write(_hbin, 0, _hbinLength);
        }

        return true;
    }

    private void CheckhbinSize(int recordSize)
    {
        if (_currentOffsetInHbin + recordSize > _hbinLength)
        {
            //we need to add another hbin

            //set remaining space to free record
            var freeSize = _hbinLength - _currentOffsetInHbin;
            if (freeSize > 0) BitConverter.GetBytes(freeSize).CopyTo(_hbin, _currentOffsetInHbin);

            //go to end of current _hbin
            _currentOffsetInHbin = _hbinLength;

            //we have to make our hbin at least as big as the data that needs to go in it, so figure that out
            var hbinBaseSize = (int) Math.Ceiling(recordSize / (double) 4096);
            var hbinSize = hbinBaseSize * 0x1000;

            //add more space
            AppendHbin(hbinSize);

            //move pointer to next usable space
            _currentOffsetInHbin += 0x20;
        }
    }

    private int ProcessSkRecord(uint skIndex)
    {
        if (!_hive.CellRecords.ContainsKey(skIndex)) return 0;

        var sk = _hive.CellRecords[skIndex] as SkCellRecord;

        if (_skMap.ContainsKey(sk.RelativeOffset))
            //sk is already in _hbin
            return _skMap[sk.RelativeOffset];

        CheckhbinSize(sk.RawBytes.Length);
        sk.RawBytes.CopyTo(_hbin, _currentOffsetInHbin);

        var skOffset = _currentOffsetInHbin;
        _skMap.Add(sk.RelativeOffset, skOffset);
        _currentOffsetInHbin += sk.RawBytes.Length;
        return skOffset;
    }

    private int ProcessClassCell(uint classcellId)
    {
        //todo make this work
        return 0;

//            if (classcellId == 0)
//            {
//                return 0;
//            }
//
//            var dataLenBytes = _hive.ReadBytesFromHive(classcellId + 4096, 4);
//            var dataLen = BitConverter.ToUInt32(dataLenBytes, 0);
//            var size = (int) dataLen;
//            size = Math.Abs(size);
//
//            var dn = new DataNode(_hive.ReadBytesFromHive(classcellId + 4096, size), classcellId);
//
//            //write it out, return offset
//            CheckhbinSize(dn.RawBytes.Length);
//            dn.RawBytes.CopyTo(_hbin, _currentOffsetInHbin);
//
//            _currentOffsetInHbin += dn.RawBytes.Length;
//
//            return _currentOffsetInHbin;
    }

    private int ProcessValue(KeyValue value)
    {
        const uint dwordSignMask = 0x80000000;

        var vkBytes = value.VkRecord.RawBytes;

        if ((value.VkRecord.DataLength & dwordSignMask) != dwordSignMask)
        {
            //non-resident data, so write out the data and update the vkrecords pointer to said data

            if (value.VkRecord.DataLength > 16344)
            {
                //big data baby!

                //big data case
                //get data and slack
                //split into 16344 chunks
                //add 4 bytes of padding at the end of each chunk
                //this makes each chunk 16348 of data plus 4 bytes at front for size (16352 total)
                //write out data chunks, keeping a record of where they went
                //build db list
                //point vk record ValueDataOffset to this location

                var dataraw = value.ValueDataRaw.Concat(value.ValueSlackRaw).ToArray();

                var pos = 0;

                var chunks = new List<byte[]>();

                while (pos < dataraw.Length)
                {
                    if (dataraw.Length - pos < 16344)
                    {
                        //we are out of data
                        chunks.Add(dataraw.Skip(pos).Take(dataraw.Length - pos).ToArray());
                        pos = dataraw.Length;
                    }

                    chunks.Add(dataraw.Skip(pos).Take(16344).ToArray());
                    pos += 16344;
                }

                var dbOffsets = new List<int>();

                foreach (var chunk in chunks)
                {
                    var rawChunk = chunk.Concat(new byte[4]).ToArray(); //add our extra 4 bytes at the end
                    var toWrite = BitConverter.GetBytes(-1 * (rawChunk.Length + 4)).Concat(rawChunk).ToArray();
                    //add the size

                    CheckhbinSize(toWrite.Length);
                    toWrite.CopyTo(_hbin, _currentOffsetInHbin);

                    dbOffsets.Add(_currentOffsetInHbin);

                    _currentOffsetInHbin += toWrite.Length;
                }


                //next is the list itself of offsets to the data chunks

                var offsetSize = 4 + dbOffsets.Count * 4; //size itself plus a slot for each offset

                if ((4 + offsetSize) % 8 != 0) offsetSize += 4;

                var offsetList =
                    BitConverter.GetBytes(-1 * offsetSize).Concat(new byte[dbOffsets.Count * 4]).ToArray();

                var i = 1;
                foreach (var dbo in dbOffsets)
                {
                    BitConverter.GetBytes(dbo).CopyTo(offsetList, i * 4);
                    i += 1;
                }

                //write offsetList to hbin
                CheckhbinSize(offsetList.Length);

                var offsetOffset = _currentOffsetInHbin;

                offsetList.CopyTo(_hbin, offsetOffset);
                _currentOffsetInHbin += offsetList.Length;


                //all the data is written, build a dblist to reference it
                //db list is just an offset to offsets
                //size db #entries offset

                var dbRaw =
                    BitConverter.GetBytes(-16)
                        .Concat(Encoding.ASCII.GetBytes("db"))
                        .Concat(
                            BitConverter.GetBytes((short) dbOffsets.Count)
                                .Concat(BitConverter.GetBytes(offsetOffset)))
                        .Concat(new byte[4])
                        .ToArray();

                var dbOffset = _currentOffsetInHbin;
                CheckhbinSize(dbRaw.Length);
                dbRaw.CopyTo(_hbin, dbOffset);

                _currentOffsetInHbin += dbRaw.Length;

                BitConverter.GetBytes(dbOffset).CopyTo(vkBytes, ValueDataOffset);
            }
            else
            {
                //TODO pull function out of here to write data and return offset
                var dataraw = value.ValueDataRaw.Concat(value.ValueSlackRaw).ToArray();

                var datarawBytes = new byte[4 + dataraw.Length];

                BitConverter.GetBytes(-1 * datarawBytes.Length).CopyTo(datarawBytes, 0);
                dataraw.CopyTo(datarawBytes, 4);

                CheckhbinSize(datarawBytes.Length);
                datarawBytes.CopyTo(_hbin, _currentOffsetInHbin);

                BitConverter.GetBytes(_currentOffsetInHbin).CopyTo(vkBytes, ValueDataOffset);

                _currentOffsetInHbin += datarawBytes.Length;
            }
        }

        CheckhbinSize(vkBytes.Length);

        var vkOffset = _currentOffsetInHbin;

        //update size of value so its found by other tools
        //TODO does this need to be optional?
        BitConverter.GetBytes(-1 * vkBytes.Length).CopyTo(vkBytes, 0);

        vkBytes.CopyTo(_hbin, vkOffset);

        _currentOffsetInHbin += vkBytes.Length;

        return vkOffset
            ;
    }

    private int ProcessKey(RegistryKey key, int parentCellIndex, bool addValues, bool addSubkeys)
    {
        // ProcessSkeletonTree() already walks the entire SkeletonKey tree built by BuildKeyTree() (which, for
        // a Recursive AddEntry, is a full flattening of the real hive's subtree), AND ProcessKey() below
        // independently re-walks the real key.SubKeys for every key it processes (addSubkeys is always true
        // here). Combined, that means every key gets fully reprocessed -- along with its entire subtree --
        // once for every ancestor level above it in the SkeletonKey tree, which is an O(depth) multiplier on
        // top of an already O(n) walk. For a hive with 1M+ keys under one bloated parent this made the write
        // phase effectively never finish. Memoizing by key path makes each real key get written exactly once:
        // subsequent (redundant) visits just return the offset already recorded for it.
        if (_processedKeyOffsets.TryGetValue(key.KeyPath, out var cachedOffset)) return cachedOffset;

        _keysWritten += 1;
        if (_keysWritten % WriteProgressLogIntervalKeys == 0)
        {
            _writeProgressStopwatch ??= Stopwatch.StartNew();
            var elapsed = _writeProgressStopwatch.Elapsed;
            var rate = elapsed.TotalSeconds > 0 ? _keysWritten / elapsed.TotalSeconds : 0;
            Log.Information(
                "Writing progress: {KeysWritten:N0} keys written so far ({ElapsedSeconds:N0}s elapsed, ~{Rate:N0} keys/sec, hbinBytes={HbinLen:N0}, managedMB={ManagedMB:N0})",
                _keysWritten, elapsed.TotalSeconds, rate, _hbinLength, GC.GetTotalMemory(false) / 1024 / 1024);
        }

        var skOffset = ProcessSkRecord(key.NkRecord.SecurityCellIndex);

        var classOffset = ProcessClassCell(key.NkRecord.ClassCellIndex);

        //do we have enough room left to place our NK?
        CheckhbinSize(key.NkRecord.RawBytes.Length);

        //this is where we will be placing our record
        var nkOffset = _currentOffsetInHbin;

        //move our pointer to the beginning of free space for any subsequent records
        CheckhbinSize(key.NkRecord.RawBytes.Length);
        _currentOffsetInHbin += key.NkRecord.RawBytes.Length;

        var nkBytes = key.NkRecord.RawBytes;

        //processValues

        BitConverter.GetBytes(0)
            .CopyTo(nkBytes, ValueCountIndex); // zero out value count unless its required for this key
        BitConverter.GetBytes(0)
            .CopyTo(nkBytes, ValueListCellIndex); // zero out value list unless its required for this key
        if (addValues)
        {
            var valueOffsets = new List<int>();

            foreach (var keyValue in key.Values)
            {
                var valOffset = ProcessValue(keyValue);

                valueOffsets.Add(valOffset);
            }

            var valueListBytes = BuildValueList(valueOffsets);
            CheckhbinSize(valueListBytes.Length);
            valueListBytes.CopyTo(_hbin, _currentOffsetInHbin);

            //update NK record to point to our new list of values and update the value count
            BitConverter.GetBytes(_currentOffsetInHbin).CopyTo(nkBytes, ValueListCellIndex);
            BitConverter.GetBytes(valueOffsets.Count).CopyTo(nkBytes, ValueCountIndex);

            _currentOffsetInHbin += valueListBytes.Length;
        }

        //processSubkeys

        BitConverter.GetBytes(0)
            .CopyTo(nkBytes, SubkeyCountStableOffset); // zero out subkey count unless its required for this key
        BitConverter.GetBytes(0)
            .CopyTo(nkBytes, SubkeyListsStableCellIndex); // zero out subkey list unless its required for this key
        if (addSubkeys)
        {
            var subkeyOffsets = new Dictionary<int, string>();

            var subKeysToInclude = key.SubKeys.Where(sk => !IsExcluded(sk.KeyPath)).ToList();

            foreach (var registryKey in subKeysToInclude)
            {
                var subkeyOffset = ProcessKey(registryKey, nkOffset, addValues, true);

                var hash = registryKey.KeyName;
                if (registryKey.KeyName.Length >= 4) hash = registryKey.KeyName.Substring(0, 4);

                //generate list for key offsets
                subkeyOffsets.Add(subkeyOffset, hash);
            }

            //TODO test the ri-chaining path below with additional real-world hives that have >500 subkeys under a single key

            //write list and save address (chunks into multiple lf lists + a chaining ri list when needed)
            var subkeyListOffset = WriteSubkeyList(subkeyOffsets);

            //update nk record pointers to subkeylist and subkey count
            BitConverter.GetBytes(subkeyListOffset).CopyTo(nkBytes, SubkeyListsStableCellIndex);
            BitConverter.GetBytes(subkeyOffsets.Count).CopyTo(nkBytes, SubkeyCountStableOffset);

            //update size to always be negative so it shows up in other tools
            //TODO maybe this needs to be optional
            BitConverter.GetBytes(-1 * nkBytes.Length).CopyTo(nkBytes, 0);
        }

        //update nkBytes

        if ((key.NkRecord.Flags & NkCellRecord.FlagEnum.HiveEntryRootKey) != NkCellRecord.FlagEnum.HiveEntryRootKey)
            //update parent offset since this isnt the root cell
            BitConverter.GetBytes(parentCellIndex).CopyTo(nkBytes, ParentCellIndex);

        BitConverter.GetBytes(skOffset).CopyTo(nkBytes, SecurityOffset);
        BitConverter.GetBytes(classOffset).CopyTo(nkBytes, ClassOffset);


        //commit our nk record to hbin
        CheckhbinSize(nkBytes.Length);
        nkBytes.CopyTo(_hbin, nkOffset);

        _processedKeyOffsets[key.KeyPath] = nkOffset;

        return nkOffset;
    }

    private byte[] BuildValueList(IReadOnlyCollection<int> offsets)
    {
        var valListSize = 4 + offsets.Count * 4;
        if (valListSize % 8 != 0) valListSize += 4;

        var offsetListBytes = new byte[valListSize];

        BitConverter.GetBytes(-1 * valListSize).CopyTo(offsetListBytes, 0);

        var index = 4;
        foreach (var valueOffset in offsets)
        {
            BitConverter.GetBytes(valueOffset).CopyTo(offsetListBytes, index);
            index += 4;
        }

        return offsetListBytes;
    }

    private int ProcessSkeletonTree(SkeletonKey treeKey)
    {
        //call processSkel for treekey.treepath
        //call for each subkey in subkeys
        //drop addsubkeys param as you will only ever be adding a key as found in tk.subkeys or keypath
        //this is where we need to build a list of subkey offsets so we can adjust SubkeyCountStableOffset and SubkeyListsStableCellIndex
        //once we know these and write it, jump to location of key and update accordingly

        Debug.WriteLine($"Processing {treeKey.KeyPath}. AddValues: {treeKey.AddValues}");

        // BuildKeyTree() flattens *every* key added via AddEntry(..., Recursive = true) into the SkeletonKey
        // tree, including ones later marked excluded via ExcludeSubtree(). ProcessKey()'s own subkey-walk
        // already filters excluded subkeys out of the *linked* subkey list via IsExcluded(), but that alone
        // doesn't stop this recursion from still descending into (and fully writing out) every excluded
        // key's NK/VK/SK cells -- for a prune of ~1M excluded keys that meant the vast majority of the write
        // phase's work was wasted writing cells that would never be reachable from any parent. Skip
        // recursing into excluded subtrees entirely.
        if (IsExcluded(treeKey.KeyPath)) return -1;

        foreach (var skeletonKey in treeKey.Subkeys) ProcessSkeletonTree(skeletonKey);

        var key = _hive.GetKey(treeKey.KeyPath);

        if (key == null)
        {
            throw new InvalidOperationException($"ProcessSkeletonTree: could not resolve key for path '{treeKey.KeyPath}'");
        }

        var parentOffset = ProcessKey(key, -1, treeKey.AddValues, true);

        return parentOffset;
    }


    private bool IsExcluded(string keyPath)
    {
        if (_excludedKeyPaths.Count == 0) return false;

        // Walk up keyPath's own ancestor chain, checking each ancestor for exact membership in
        // _excludedKeyPaths (an O(1) HashSet lookup). This correctly detects both an exact match and
        // "keyPath is a descendant of an excluded subtree root" in O(depth) time, regardless of how many
        // paths are excluded. The previous implementation scanned every excluded path per call
        // (StartsWith), which made pruning O(totalKeys * excludedCount) -- for real-world cases where a
        // large flat set of individually-named leaf subkeys is excluded (e.g. ~1M DiagConnectionCache
        // subkeys), that was effectively O(n^2) and never finished in practice.
        var candidate = keyPath;
        while (true)
        {
            if (_excludedKeyPaths.Contains(candidate)) return true;

            var lastSlash = candidate.LastIndexOf('\\');
            if (lastSlash < 0) break;

            candidate = candidate.Substring(0, lastSlash);
        }

        return false;
    }

    /// <summary>
    ///     Writes the given subkey offset/hash pairs as one or more "lf" list records, chunked into groups of
    ///     at most 500 entries (a real-world Windows-observed convention), chained together via an "ri" list
    ///     record when more than one chunk is required. Returns the relative offset of whichever list record
    ///     the owning NK's SubkeyListsStableCellIndex should point to (either the single "lf" list, or the "ri"
    ///     list if chunking was needed).
    /// </summary>
    private int WriteSubkeyList(Dictionary<int, string> subkeyOffsets)
    {
        const int maxEntriesPerLfList = 500;

        if (subkeyOffsets.Count <= maxEntriesPerLfList)
        {
            var lfBytes = BuildlfList(subkeyOffsets);
            CheckhbinSize(lfBytes.RawBytes.Length);
            var lfOffset = _currentOffsetInHbin;
            lfBytes.RawBytes.CopyTo(_hbin, lfOffset);
            _currentOffsetInHbin += lfBytes.RawBytes.Length;
            return lfOffset;
        }

        // Chunk into groups of maxEntriesPerLfList, write each as its own "lf" list, then chain them via "ri".
        var chunkOffsets = new List<int>();
        var chunk = new Dictionary<int, string>();

        foreach (var entry in subkeyOffsets)
        {
            chunk.Add(entry.Key, entry.Value);

            if (chunk.Count == maxEntriesPerLfList)
            {
                chunkOffsets.Add(WriteLfChunk(chunk));
                chunk = new Dictionary<int, string>();
            }
        }

        if (chunk.Count > 0)
        {
            chunkOffsets.Add(WriteLfChunk(chunk));
        }

        var riBytes = BuildRiList(chunkOffsets);
        CheckhbinSize(riBytes.Length);
        var riOffset = _currentOffsetInHbin;
        riBytes.CopyTo(_hbin, riOffset);
        _currentOffsetInHbin += riBytes.Length;

        return riOffset;
    }

    private int WriteLfChunk(Dictionary<int, string> chunk)
    {
        var lfBytes = BuildlfList(chunk);
        CheckhbinSize(lfBytes.RawBytes.Length);
        var lfOffset = _currentOffsetInHbin;
        lfBytes.RawBytes.CopyTo(_hbin, lfOffset);
        _currentOffsetInHbin += lfBytes.RawBytes.Length;
        return lfOffset;
    }

    private byte[] BuildRiList(List<int> chunkOffsets)
    {
        var totalSize = 4 + 2 + 2 + chunkOffsets.Count * 4; //size + sig + num entries + offsets

        var listBytes = new byte[totalSize];

        BitConverter.GetBytes(-1 * totalSize).CopyTo(listBytes, 0);
        Encoding.ASCII.GetBytes("ri").CopyTo(listBytes, 4);
        BitConverter.GetBytes((short) chunkOffsets.Count).CopyTo(listBytes, 6);

        var index = 0x8;
        foreach (var offset in chunkOffsets)
        {
            BitConverter.GetBytes(offset).CopyTo(listBytes, index);
            index += 4;
        }

        return listBytes;
    }

    private LxListRecord BuildlfList(Dictionary<int, string> subkeyInfo)
    {
        var totalSize = 4 + 2 + 2 + subkeyInfo.Count * 8; //size + sig + num entries + bytes for list itself

        var listBytes = new byte[totalSize];

        BitConverter.GetBytes(-1 * totalSize).CopyTo(listBytes, 0);
        Encoding.ASCII.GetBytes("lf").CopyTo(listBytes, 4);
        BitConverter.GetBytes((short) subkeyInfo.Count).CopyTo(listBytes, 6);

        var index = 0x8;

        foreach (var entry in subkeyInfo)
        {
            BitConverter.GetBytes(entry.Key).CopyTo(listBytes, index);
            index += 4;
            Encoding.ASCII.GetBytes(entry.Value).CopyTo(listBytes, index);
            index += 4;
        }

        //we can set relative offset to 0 since we are only interested in the bytes
        return new LxListRecord(listBytes, 0);
    }

    private SkeletonKey BuildKeyTree()
    {
        SkeletonKey root = null;

        foreach (var keyRoot in _keys)
        {
            var current = root;

            //need to make sure root key name is at beginning of each

            var segs = keyRoot.KeyPath.Split('\\');

            var withVals = keyRoot.AddValues;
            foreach (var seg in segs)
            {
                if (seg == segs.Last()) withVals = keyRoot.AddValues;

                if (root == null)
                {
                    root = new SkeletonKey(seg, seg, withVals);
                    current = root;
                    continue;
                }

                if (current.KeyName == segs.First() && seg == segs.First()) continue;

                if (current.SubkeysByName.TryGetValue(seg, out var existingChild))
                {
                    current = existingChild;
                    continue;
                }

                if (seg == segs.Last()) withVals = keyRoot.AddValues;

                var sk = new SkeletonKey($"{current.KeyPath}\\{seg}", seg, withVals);
                current.AddSubkey(sk);
                current = sk;
            }
        }

        return root;
    }
}

public class SkeletonKeyRoot
{
    public SkeletonKeyRoot(string keyPath, bool addValues, bool recursive)
    {
        KeyPath = keyPath;
        AddValues = addValues;
        Recursive = recursive;
    }

    public string KeyPath { get; }
    public bool AddValues { get; }
    public bool Recursive { get; }
}

public class SkeletonKey
{
    public SkeletonKey(string keyPath, string keyName, bool addValues)
    {
        KeyPath = keyPath;
        KeyName = keyName;
        AddValues = addValues;
        Subkeys = new List<SkeletonKey>();
        // Case-insensitive index of Subkeys by KeyName, kept in sync with the Subkeys list. Registry key
        // names are case-insensitive, and BuildKeyTree() needs to find/create a child by name for every
        // segment of every flattened key path -- for hives with very large sibling counts (e.g. 1M+
        // subkeys under one parent) a linear Subkeys.Any()/Single() scan per lookup made tree
        // reconstruction effectively O(n^2). This index makes that lookup O(1) instead.
        SubkeysByName = new Dictionary<string, SkeletonKey>(StringComparer.OrdinalIgnoreCase);
    }

    public string KeyName { get; }
    public string KeyPath { get; }
    public bool AddValues { get; }
    public List<SkeletonKey> Subkeys { get; }
    public Dictionary<string, SkeletonKey> SubkeysByName { get; }

    public void AddSubkey(SkeletonKey child)
    {
        Subkeys.Add(child);
        SubkeysByName[child.KeyName] = child;
    }
}
