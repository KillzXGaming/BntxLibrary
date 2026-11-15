using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace BntxLibrary.IO
{
    /// <summary>
    /// Binary reader for BNTX.
    /// </summary>
    internal class FileReader : BinaryReader
    {
        public bool BigEndian { get; set; }

        public long Position
        {
            get => BaseStream.Position;
            set => BaseStream.Position = value;
        }

        public FileReader(string filePath) : base(new FileStream(filePath, FileMode.Open, FileAccess.Read))
        {
        }

        public FileReader(Stream input, bool leaveOpen = false) : base(input, Encoding.UTF8, leaveOpen)
        {
        }

        public override short ReadInt16()
            => BigEndian ? BinaryPrimitives.ReverseEndianness(base.ReadInt16()) : base.ReadInt16();

        public override ushort ReadUInt16() 
            => BigEndian ? BinaryPrimitives.ReverseEndianness(base.ReadUInt16()) : base.ReadUInt16();

        public override int ReadInt32() 
            => BigEndian ? BinaryPrimitives.ReverseEndianness(base.ReadInt32()) : base.ReadInt32();

        public override uint ReadUInt32()
            => BigEndian ? BinaryPrimitives.ReverseEndianness(base.ReadUInt32()) : base.ReadUInt32();

        public override long ReadInt64()
            => BigEndian ? BinaryPrimitives.ReverseEndianness(base.ReadInt64()) : base.ReadInt64();

        public override ulong ReadUInt64()
            => BigEndian ? BinaryPrimitives.ReverseEndianness(base.ReadUInt64()) : base.ReadUInt64();
        public override float ReadSingle()
            => BigEndian
            ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReverseEndianness(base.ReadInt32()))
            : base.ReadSingle();

        public long SeekBegin(long offset) => BaseStream.Seek(offset, SeekOrigin.Begin);

        public TemporarySeekHandle TemporarySeek(long offset, SeekOrigin origin)
            => BaseStream.TemporarySeek(offset, origin);

        public void ReadSignature(string expected)
            => CheckSignature(Encoding.ASCII.GetString(ReadBytes(expected.Length)), expected);

        public void CheckSignature(string magic, params string[] expected)
        {
            if (Array.IndexOf(expected, magic) < 0)
                throw new Exception($"Expected {string.Join(" or ", expected)} but got {magic} instead.");
        }

        public string ReadStringZeroTerminated(Encoding? encoding = null)
        {
            var bytes = new List<byte>();
            int b;
            while ((b = BaseStream.ReadByte()) > 0)
                bytes.Add((byte)b);
            return (encoding ?? Encoding.UTF8).GetString(bytes.ToArray());
        }

        public ulong[] ReadUInt64s(int count)
        {
            var result = new ulong[count];
            for (int i = 0; i < count; i++)
                result[i] = ReadUInt64();
            return result;
        }

        public unsafe T ReadStruct<T>() where T : struct
        {
            byte[] buffer = ReadBytes(Marshal.SizeOf<T>());
            if (buffer.Length < Marshal.SizeOf<T>())
                throw new EndOfStreamException($"Unexpected end of stream while reading {typeof(T).Name}.");

            fixed (byte* p = buffer)
                return Marshal.PtrToStructure<T>((IntPtr)p);
        }

        public List<T> ReadMultipleStructs<T>(int count) where T : struct
        {
            var list = new List<T>(count);
            for (int i = 0; i < count; i++)
                list.Add(ReadStruct<T>());
            return list;
        }
    }


    /// <summary>
    /// Binary writer for BNTX.
    /// </summary>
    internal class FileWriter : BinaryWriter
    {
        public bool BigEndian { get; set; }

        public long Position
        {
            get => BaseStream.Position;
            set => BaseStream.Position = value;
        }

        public FileWriter(string filePath) : base(new FileStream(filePath, FileMode.Create, FileAccess.Write))
        {
        }

        public FileWriter(Stream input, bool leaveOpen = true) : base(input, Encoding.UTF8, leaveOpen)
        {
        }

        public override void Write(short value) 
            => base.Write(BigEndian ? BinaryPrimitives.ReverseEndianness(value) : value);

        public override void Write(ushort value) 
            => base.Write(BigEndian ? BinaryPrimitives.ReverseEndianness(value) : value);

        public override void Write(int value) 
            => base.Write(BigEndian ? BinaryPrimitives.ReverseEndianness(value) : value);

        public override void Write(uint value) 
            => base.Write(BigEndian ? BinaryPrimitives.ReverseEndianness(value) : value);

        public override void Write(long value)
            => base.Write(BigEndian ? BinaryPrimitives.ReverseEndianness(value) : value);

        public override void Write(ulong value) 
            => base.Write(BigEndian ? BinaryPrimitives.ReverseEndianness(value) : value);
        public override void Write(float value)
                => base.Write(BigEndian ?
                    BinaryPrimitives.ReverseEndianness(BitConverter.SingleToInt32Bits(value)) 
                    : value);

        public TemporarySeekHandle TemporarySeek(long offset, SeekOrigin origin)
            => BaseStream.TemporarySeek(offset, origin);

        public TemporarySeekHandle TemporarySeek()
            => BaseStream.TemporarySeek();

        public void WriteSignature(string signature) => Write(Encoding.ASCII.GetBytes(signature));

        public void WriteZeroTerminatedString(string value, Encoding? encoding = null)
        {
            Write((encoding ?? Encoding.UTF8).GetBytes(value));
            Write((byte)0);
        }

        public void WriteSectionSizeU32(long position, long size)
        {
            using (TemporarySeek(position, SeekOrigin.Begin))
                Write((uint)size);
        }

        public void Align(int alignment, byte value = 0x00)
        {
            int padding = (int)((alignment - Position % alignment) % alignment);
            if (padding == 0)
                return;

            var bytes = new byte[padding];
            if (value != 0)
                Array.Fill(bytes, value);
            Write(bytes);
        }

        public void Write(ulong[] values)
        {
            foreach (ulong value in values)
                Write(value);
        }

        public unsafe void WriteStruct<T>(T item) where T : struct
        {
            var buffer = new byte[Marshal.SizeOf<T>()];
            fixed (byte* p = buffer)
                Marshal.StructureToPtr(item, (IntPtr)p, false);
            Write(buffer);
        }

        public void WriteMultiStruct<T>(List<T> list) where T : struct
        {
            foreach (T item in list)
                WriteStruct(item);
        }
    }

    internal static class StreamExtension
    {
        public static TemporarySeekHandle TemporarySeek(this Stream stream, long offset, SeekOrigin origin)
        {
            long ret = stream.Position;
            stream.Seek(offset, origin);
            return new TemporarySeekHandle(stream, ret);
        }

        public static TemporarySeekHandle TemporarySeek(this Stream stream)
        {
            long ret = stream.Position;
            return new TemporarySeekHandle(stream, ret);
        }
    }

    internal readonly ref struct TemporarySeekHandle
    {
        private readonly Stream Stream;
        private readonly long RetPos;

        public TemporarySeekHandle(Stream stream, long retpos)
        {
            this.Stream = stream;
            this.RetPos = retpos;
        }

        public readonly void Dispose()
        {
            Stream.Seek(RetPos, SeekOrigin.Begin);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct Magic
    {
        int value;
        public static implicit operator string(Magic magic) => Encoding.ASCII.GetString(BitConverter.GetBytes(magic.value));
        public static implicit operator Magic(string s) => new Magic { value = BitConverter.ToInt32(Encoding.ASCII.GetBytes(s), 0) };
    }
}
