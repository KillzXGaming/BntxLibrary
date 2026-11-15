using BntxLibrary.IO;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace BntxLibrary
{
    internal class BntxWriter
    {
        static Dictionary<string, long> _stringTable = [];
        static Dictionary<long, long> _pointers = []; // position, then target (if written)

        static List<(long, long)> _rltSectionRanges = new List<(long, long)>();
        static List<long> _blocks = new List<long>();

        public static void Write(BntxFile bntx, FileWriter writer)
        {
            _pointers = new Dictionary<long, long>();
            _stringTable.Clear();
            _rltSectionRanges.Clear();
            _blocks.Clear();

            switch (bntx.Platform)
            {
                case PlatformKind.NX: bntx.BntxHeader.Magic = "NX  "; break;
                case PlatformKind.PC: bntx.BntxHeader.Magic = "PC  "; break;
                case PlatformKind.Ounce: bntx.BntxHeader.Magic = "Ounc"; break;
            }

            var section1Start = writer.Position;

            writer.BigEndian = bntx.BigEndian;
            writer.WriteStruct(bntx.BinHeader);

            // Block offset is 0x16 (ushort) in the bin header
            _blocks.Add(0x16);

            long _ofsStartBntxHeader = writer.Position;
            writer.WriteStruct(bntx.BntxHeader);

            WriteOffset(writer, _ofsStartBntxHeader + 32);
            writer.Write(new byte[0x140]); // Memory pool
            // Write offsets
            writer.Align(8);
            long _ofsStartImagePointers = writer.Position;
            WriteOffset(writer, _ofsStartBntxHeader + 8);
            writer.Write(new ulong[bntx.Textures.Count]);
            // String table
            WriteStringTable(writer, bntx);

            // Dictionary
            writer.Align(8);
            WriteOffset(writer, _ofsStartBntxHeader + 24);
            WriteDictionary(writer, bntx.Textures.Keys.ToList());

            List<long> dataOffsets = new List<long>();

            // Texture headers
            int textureIndex = 0;
            foreach (var tex in bntx.Textures.Values)
            {
                tex.Header.NameOffset = (ulong)_stringTable[tex.Name];
                // Image size is the same as the data length of the image data stream
                tex.Header.ImageSize = (uint)tex.ImageData.Length;
                // Offsets pre-calculated
                tex.Header.BntxHeaderOffset = (ulong)_ofsStartBntxHeader; 
                tex.Header.TexturePointer = (ulong)writer.Position + 0x90 + 16;
                tex.Header.TextureViewPointer = (ulong)writer.Position + 0x190+ 16;
                // Todo slot section
                tex.Header.DescSlotOffset = 0UL;

                if (tex.UserData.Count == 0)
                {
                    tex.Header.UserDataOffset = 0;
                    tex.Header.UserDataDictionaryOffset = 0;
                }

                var headerStart = writer.Position;
                WriteOffset(writer, _ofsStartImagePointers + textureIndex * 8);
                writer.WriteStruct(tex.Header);

                // Write block header at 0x4
                using (writer.TemporarySeek(headerStart + 4, SeekOrigin.Begin)) {
                    WriteBlock(writer);
                }

                writer.Write(new byte[256]); // texture runtime data
                writer.Write(new byte[256]); // texture view runtime data

                tex.Header.DataPointersOffset = (ulong)writer.Position;

                // Add offsets for relocation
                _pointers.TryAdd(headerStart + 96, (long)tex.Header.NameOffset);
                _pointers.TryAdd(headerStart + 96 + 8, (long)tex.Header.BntxHeaderOffset);
                _pointers.TryAdd(headerStart + 96 + 16, (long)tex.Header.DataPointersOffset);
                _pointers.TryAdd(headerStart + 96 + 24, (long)tex.Header.UserDataOffset);

                _pointers.TryAdd(headerStart + 96 + 32, (long)tex.Header.TexturePointer);
                _pointers.TryAdd(headerStart + 96 + 40, (long)tex.Header.TextureViewPointer);
                _pointers.TryAdd(headerStart + 96 + 48, (long)tex.Header.DescSlotOffset);
                _pointers.TryAdd(headerStart + 96 + 56, (long)tex.Header.UserDataDictionaryOffset);

                WriteOffset(writer, headerStart + 96 + 16);
                dataOffsets.Add(writer.Position);
                writer.Write(new ulong[tex.Header.MipCount]); // Mip offsets
                WriteUserData(writer, tex, headerStart);
                textureIndex++;
            }

            writer.Align(8);
            _rltSectionRanges.Add((section1Start, writer.Position));

            // Data
            writer.Align(bntx.Alignment);
            writer.Seek(-16, SeekOrigin.Current);

            var section2Start = writer.Position;
            WriteOffset(writer, _ofsStartBntxHeader + 16);
            WriteDataBlockTable(writer, bntx, dataOffsets);

            writer.Align(bntx.Alignment);

            // Blocks
            using (writer.TemporarySeek())
            {
                WriteBlockHeaders(writer, writer.Position + 4);
            }

            // Relocation table
            _rltSectionRanges.Add((section2Start, writer.Position));

            bntx._relocationTable.Build(_pointers.Select(x => new Pointer()
            {
                Position = x.Key,
                Target = x.Value,
            }).ToList(), _rltSectionRanges, writer.Position);

            writer.WriteSectionSizeU32(24, writer.Position);
            WriteRelocationTable(writer, bntx._relocationTable);

            // Bntx size
            writer.WriteSectionSizeU32(28, writer.BaseStream.Length);

            // Apply hash related values which detect file changes for saving
            bntx._hashSize = bntx.CalculateSizeHash();
            bntx._hash = bntx.CalculateHash();
        }

        static void WriteUserData(FileWriter writer, BntxTexture tex, long headerStart)
        {
            if (tex.UserData.Count == 0)
                return;

            var entries = tex.UserData.Values.ToList();
            var headerPositions = new long[entries.Count];

            writer.Align(8);
            long entriesStart = writer.Position;
            for (int i = 0; i < entries.Count; i++)
            {
                var header = entries[i].Header;
                header.NameOffset = (ulong)_stringTable[entries[i].Name];
                header.DataOffset = 0;
                header.Count = (uint)((Array)entries[i].Value).Length;

                headerPositions[i] = writer.Position;
                _pointers[writer.Position] = (long)header.NameOffset;
                writer.WriteStruct(header);
            }

            // Values
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                int count = (int)((Array)entry.Value).Length;
                if (count == 0)
                    continue;

                writer.Align(8);
                long dataStart = writer.Position;
                switch (entry.Type)
                {
                    case UserDataType.String:
                    case UserDataType.WString:
                        foreach (string value in entry.GetValueArray<string>())
                        {
                            _pointers[writer.Position] = _stringTable[value];
                            writer.Write((ulong)_stringTable[value]);
                        }
                        break;
                    case UserDataType.Int32:
                        foreach (int value in entry.GetValueArray<int>()) writer.Write(value);
                        break;
                    case UserDataType.Single:
                        foreach (float value in entry.GetValueArray<float>()) writer.Write(value);
                        break;
                    case UserDataType.Byte:
                        writer.Write(entry.GetValueArray<byte>());
                        break;
                }

                _pointers[headerPositions[i] + 8] = dataStart;
                using (writer.TemporarySeek(headerPositions[i] + 8, SeekOrigin.Begin))
                    writer.Write((ulong)dataStart);
            }

            // Dictionary
            writer.Align(8);
            long dictionaryStart = writer.Position;
            WriteDictionary(writer, tex.UserData.Keys.ToList());

            // Point the texture header at the entries and the dictionary
            tex.Header.UserDataOffset = (ulong)entriesStart;
            tex.Header.UserDataDictionaryOffset = (ulong)dictionaryStart;
            _pointers[headerStart + 96 + 24] = entriesStart;
            _pointers[headerStart + 96 + 56] = dictionaryStart;
            using (writer.TemporarySeek(headerStart + 96 + 24, SeekOrigin.Begin))
                writer.Write((ulong)entriesStart);
            using (writer.TemporarySeek(headerStart + 96 + 56, SeekOrigin.Begin))
                writer.Write((ulong)dictionaryStart);
        }

        static void WriteBlockHeaders(FileWriter writer, long endOfBlock)
        {
            for (int i = 0; i < _blocks.Count; i++)
            {
                var position = _blocks[i];
                writer.Position = position;

                if (i == 0)
                {
                    writer.Write((ushort)(_blocks[1] - 4)); //Write the next block offset
                }
                else if (i == _blocks.Count - 1)
                {
                    writer.Write(0);
                    writer.Write((long)(endOfBlock - _blocks[i])); //Size of string table to relocation table
                }
                else
                {
                    if (i < _blocks.Count - 1)
                    {
                        uint blockSize = (uint)(_blocks[i + 1] - _blocks[i]);
                        writer.Write((uint)blockSize);
                        writer.Write((ulong)blockSize);
                    }
                }
            }
        }

        static void WriteStringTable(FileWriter writer, BntxFile bntx)
        {
            // Here we store all possible strings from the binary first as string table is written first
            long _ofsFileNameString = 0;

            Dictionary<string, Encoding> bntxStrings = new Dictionary<string, Encoding>();
            foreach (var tex in bntx.Textures.Values)
            {
                bntxStrings.TryAdd(tex.Name, null);
                foreach (var userdata in tex.UserData.Values)
                {
                    bntxStrings.TryAdd(userdata.Name, null);

                    // String values are pointers into the string table
                    if (userdata.Type != UserDataType.String &&
                       userdata.Type != UserDataType.WString)
                        continue;

                    foreach (var data in userdata.GetValueArray<string>())
                        bntxStrings.TryAdd(data, BntxFile.StringEncoding);
                }
            }
            bntxStrings.TryAdd(bntx.Name, null);
            bntxStrings.TryAdd("", null);

            var order = bntx.Strings.OrderBy(x => x.Key).Select(x => x.Value).ToList();

            Dictionary<string, Encoding> sorted = new Dictionary<string, Encoding>();

            // The empty string always comes first, strings new to the file go after the existing ones
            foreach (KeyValuePair<string, Encoding> entry in bntxStrings.OrderBy(x =>
            {
                if (x.Key == "") return -1;
                int i = order.IndexOf(x.Key);
                return i < 0 ? int.MaxValue : i;
            }))
            {
                sorted.Add(entry.Key, entry.Value);
            }

            writer.Align(4);
            uint stringPoolStart = (uint)writer.Position;
            writer.WriteSignature("_STR");
            WriteBlock(writer);
            writer.Write(sorted.Count - 1);
            foreach (var entry in sorted)
            {
                if (entry.Key == bntx.Name)
                    _ofsFileNameString = (long)writer.Position;

                _stringTable.Add(entry.Key, writer.Position);

                var encoding = entry.Value ?? BntxFile.StringEncoding;
                writer.Write((short)encoding.GetByteCount(entry.Key)); // length in bytes
                // Write the name.
                writer.WriteZeroTerminatedString(entry.Key, encoding);

                writer.Align(2);
            }
            //Save the file name. Goes directly to name instead of size
            using (writer.TemporarySeek(16, SeekOrigin.Begin))
            {
                if (_ofsFileNameString != 0)
                    writer.Write((uint)(_ofsFileNameString + 2));
            }
        }

        static void WriteDataBlockTable(FileWriter writer, BntxFile bntx, List<long> dataOffsets)
        {
            writer.WriteSignature("BRTD");
            WriteBlock(writer);

            int index = 0;
            foreach (var tex in bntx.Textures.Values)
            {
                var startOffset = writer.Position;
                using (writer.TemporarySeek(dataOffsets[index], SeekOrigin.Begin)) {
                    for (int i = 0; i < tex.MipOffsets.Length; i++)
                    {
                        var dataPos = startOffset + (long)tex.MipOffsets[i];

                        _pointers.TryAdd(writer.Position, dataPos);
                        _pointers[writer.Position] = dataPos;

                        writer.Write((ulong)dataPos);
                    }
                    index++;
                }
                writer.Write(tex.ImageData.ToArray());
            }
        }

        static void WriteDictionary(FileWriter writer, List<string> keys)
        {
            var nodes = ResDict.UpdateNodes(keys);

            writer.WriteSignature("_DIC");
            writer.Write(nodes.Length - 1);
            for (int i = 0; i < nodes.Length; i++)
            {
                writer.Write(nodes[i].Reference);
                writer.Write(nodes[i].IdxLeft);
                writer.Write(nodes[i].IdxRight);
                SaveNameOffset(i == 0 ? "" : nodes[i].Key, writer);
            }
        }

        static void WriteRelocationTable(FileWriter writer, RelocationTable table)
        {
            writer.WriteSignature("_RLT");
            writer.Write((uint)(writer.Position - 4));
            writer.Write(table.Sections.Count);
            writer.Write(0); // padding
            writer.WriteMultiStruct(table.Sections);
            writer.WriteMultiStruct(table.Entries);
        }

        static void SaveNameOffset(string name, FileWriter writer) {
            _pointers.TryAdd(writer.Position, 0);

            if (name == null)
            {
                writer.Write(0UL);
                return;
            }

            var pos = writer.Position;
            // String table written already with strings, so ensure the string is present already
            if (!_stringTable.ContainsKey(name))
                throw new Exception();

            _pointers[writer.Position] = _stringTable[name];
            writer.Write(_stringTable[name]);
        }

        static void WriteBlock(FileWriter writer)
        {
            _blocks.Add(writer.Position);

            // Block header
            writer.Write(0); // size
            writer.Write(0UL); // offset
        }

        static void WriteOffset(FileWriter writer, long pos)
        {
            _pointers[pos] = writer.Position;

            var ofs = writer.Position;
            using (writer.TemporarySeek(pos, SeekOrigin.Begin))
                writer.Write(ofs);
        }
    }
}
