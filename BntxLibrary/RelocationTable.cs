using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace BntxLibrary
{
    public class RelocationTable
    {
        public List<Section> Sections = new List<Section>();
        public List<Entry> Entries = new List<Entry>();

        [StructLayout(LayoutKind.Sequential, Size = 0x10)]
        public struct Section // Section where pointers are pointing to
        {
            public ulong Pointer; // 0
            public uint Offset; // section position
            public int Size; // section size
            public int FirstEntryIndex; // entry index 
            public int EntryCount; // entry count
        }

        [StructLayout(LayoutKind.Sequential, Size = 8)]
        public struct Entry
        {
            public int PointersOffset; // start of pointers
            public ushort ArrayCount; // structure count in array
            public byte PointerCount; // total pointer count as uint64
            public byte PaddingCount; // padding count as uint64. Reads to the next structure offset
        }

        public void Build(List<Pointer> pointers, List<(long, long)> sectionRanges, long sectionEnd)
        {
            this.Sections.Clear();
            this.Entries.Clear();

            pointers = pointers.Where(x => x.Target != 0).ToList();
            for (int s = 0; s < sectionRanges.Count; s++)
            {
                var rangeStart = sectionRanges[s].Item1;
                var rangeEnd = sectionRanges[s].Item2;
                // Range of pointers used for the section
                var sectionPointers = pointers.Where(x =>
                    x.Target >= rangeStart &&
                    x.Target <= rangeEnd).OrderBy(x => x.Position).ToList();
                var entries = Build(sectionPointers);
                this.Sections.Add(new Section
                {
                    Pointer = 0, // Runtime pointer
                    Offset = (uint)rangeStart,
                    Size = (int)(rangeEnd - rangeStart),
                    FirstEntryIndex = this.Entries.Count,
                    EntryCount = entries.Count
                });
                this.Entries.AddRange(entries);
            }

            // Ensure pointers are all in range of the sections marked
            foreach (var p in pointers)
            {
                bool isRelocated = false;
                foreach (var section in this.Sections)
                {
                    var rangeStart = section.Offset;
                    var rangeEnd = section.Offset + section.Size;

                    if (p.Target >= rangeStart && p.Target <= rangeEnd)
                        isRelocated = true;
                }
                if (!isRelocated)
                    throw new Exception($"Failed to find pointer target in relocation table {p.Target}!");
            }


            // Final check, ensure all entries are relocated
            const int POINTER_SIZE = 8;

            List<long> relocatedPositions = new List<long>();
            foreach (var entry in this.Entries)
            {
                var ofs = entry.PointersOffset;
                for (int a = 0; a < entry.ArrayCount; a++)
                {
                    for (int p = 0; p < entry.PointerCount; p++)
                    {
                        relocatedPositions.Add(ofs);
                        ofs += POINTER_SIZE;
                    }
                    ofs += entry.PaddingCount * POINTER_SIZE;
                }
            }

            foreach (var p in pointers)
            {
                if (!relocatedPositions.Contains(p.Position))
                    throw new Exception($"Failed to find pointer in relocation table {p.Position}!");
            }

            Console.WriteLine($"Built relocation table with {this.Entries.Count} entries.");
        }

        public List<Entry> Build(List<Pointer> sectionPointers)
        {
            List<Entry> entries = BuildRelocationEntries(sectionPointers.Select(x => x.Position).ToList());

            // Verify
            HashSet<long> relocatedPositions = new HashSet<long>();
            foreach (var entry in entries)
            {
                var ofs = entry.PointersOffset;
                for (int a = 0; a < entry.ArrayCount; a++)
                {
                    for (int p = 0; p < entry.PointerCount; p++)
                    {
                        bool added = relocatedPositions.Add(ofs);
                        if (!added)
                            throw new Exception($"Pointer offset {ofs} was relocated more than once!");
                        ofs += PointerSize;
                    }
                    ofs += entry.PaddingCount * PointerSize;
                }
            }
            foreach (var p in sectionPointers)
            {
                if (!relocatedPositions.Contains(p.Position))
                    throw new Exception($"Failed to find pointer in relocation table {p.Position}!");
            }
            return entries;
        }

        const int PointerSize = 8;

        /// <summary>
        /// Some small AI assistance was used to help me RE/figure this out.
        /// Groups sorted pointer positions into relocation entries.
        /// Pointers next to each other form a run, and runs of the same length that repeat at a constant stride get chained together.
        /// </summary>
        private static List<Entry> BuildRelocationEntries(List<long> pointerPositions)
        {
            const int PointerSize = 8;

            // First get consecutive offset runs
            var runs = new List<(long Start, int Length)>();
            foreach (var pos in pointerPositions.Distinct().OrderBy(x => x))
            {
                if (runs.Count > 0)
                {
                    var last = runs[^1];
                    if (pos == last.Start + last.Length * PointerSize && last.Length < byte.MaxValue)
                    {
                        runs[^1] = (last.Start, last.Length + 1);
                        continue;
                    }
                }
                runs.Add((pos, 1));
            }

            var entries = new List<Entry>();

            // Now try to chain runs of the same length that have a constant stride
            foreach (var group in runs.GroupBy(r => r.Length))
            {
                int length = group.Key;
                var list = group.ToList();
                var used = new bool[list.Count];

                // Quick lookup so we can jump by stride
                var indexByStart = new Dictionary<long, int>();
                for (int i = 0; i < list.Count; i++)
                    indexByStart[list[i].Start] = i;

                for (int i = 0; i < list.Count; i++)
                {
                    if (used[i]) continue;
                    used[i] = true;

                    int count = 1;
                    long padding = 0;

                    // See if the next unused run of this length gives us a usable stride
                    int next = i + 1;
                    while (next < list.Count && used[next])
                        next++;

                    if (next < list.Count)
                    {
                        long stride = list[next].Start - list[i].Start;
                        long pad = stride / PointerSize - length;

                        if (pad <= byte.MaxValue)
                        {
                            count = 2;
                            padding = pad;
                            used[next] = true;

                            // Keep going with the same stride
                            long expected = list[next].Start + stride;
                            while (count < ushort.MaxValue &&
                                   indexByStart.TryGetValue(expected, out int found) &&
                                   !used[found])
                            {
                                used[found] = true;
                                count++;
                                expected += stride;
                            }
                        }
                    }

                    entries.Add(new Entry()
                    {
                        PointersOffset = (int)list[i].Start,
                        PointerCount = (byte)length,
                        ArrayCount = (ushort)count,
                        PaddingCount = (byte)padding,
                    });
                }
            }

            return entries.OrderBy(e => e.PointersOffset).ToList();
        }
    }

    public class Pointer
    {
        public long Position; // Where the offset is stored
        public long Target; // Where the offset pointers to
    }
}
