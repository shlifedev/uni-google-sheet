#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HolySheet.Core.Schema;

namespace HolySheet.Core.Codecs
{
    public interface ITableCodec
    {
        string Name { get; }

        byte[] Encode(TableData table);

        IReadOnlyList<string> Usings { get; }

        string RowAttribute { get; }

        string FieldAttribute(int order);

        string ConstructorAttribute { get; }

        string IgnoreAttribute { get; }

        string DeserializeExpression(string rowType, string dataVariable);
    }

    public static class TableCodecs
    {
        public static IReadOnlyList<ITableCodec> All { get; } = new ITableCodec[] { new MemoryPackCodec(), new MessagePackCodec() };

        public static ITableCodec? Find(string name) => All.FirstOrDefault(codec => codec.Name == name);
    }

    public sealed class MemoryPackCodec : ITableCodec
    {
        public const string CodecName = "memorypack";

        public string Name => CodecName;

        public IReadOnlyList<string> Usings { get; } = new[] { "MemoryPack" };

        public string RowAttribute => "[MemoryPackable(SerializeLayout.Explicit)]";

        public string FieldAttribute(int order) => $"[MemoryPackInclude, MemoryPackOrder({order})]";

        public string ConstructorAttribute => "[MemoryPackConstructor]";

        public string IgnoreAttribute => "[MemoryPackIgnore]";

        public string DeserializeExpression(string rowType, string dataVariable) => $"MemoryPackSerializer.Deserialize<{rowType}[]>({dataVariable})";

        public byte[] Encode(TableData table)
        {
            var writer = new ByteWriter(64 + table.Rows.Count * 32);
            TableSchema schema = table.Schema;
            byte memberCount = checked((byte)schema.StoredMemberCount);
            writer.Int32LittleEndian(table.Rows.Count);
            foreach (RowData row in table.Rows)
            {
                writer.Byte(memberCount);
                for (int index = 0; index < schema.Columns.Count; index++)
                {
                    ColumnType type = schema.Columns[index].Type;
                    object value = row.Values[index];
                    if (type.IsArray)
                    {
                        var elements = (object[])value;
                        writer.Int32LittleEndian(elements.Length);
                        foreach (object element in elements)
                        {
                            WriteLeaf(writer, type, element);
                        }
                    }
                    else if (type.Kind == ColumnKind.Reference)
                    {
                        var reference = (RefValue)value;
                        if (type.IsOptional)
                        {
                            writer.Byte(reference.HasValue ? (byte)1 : (byte)0);
                        }

                        WriteLeaf(writer, type, reference.Key ?? LeafValues.Default(type));
                    }
                    else
                    {
                        WriteLeaf(writer, type, value);
                    }
                }
            }

            return writer.ToArray();
        }

        private static void WriteLeaf(ByteWriter writer, ColumnType type, object value)
        {
            (ScalarKind kind, object scalar) = LeafValues.Resolve(type, value);
            switch (kind)
            {
                case ScalarKind.Bool:
                    writer.Byte((bool)scalar ? (byte)1 : (byte)0);
                    break;
                case ScalarKind.SByte:
                    writer.Byte(unchecked((byte)(sbyte)scalar));
                    break;
                case ScalarKind.Byte:
                    writer.Byte((byte)scalar);
                    break;
                case ScalarKind.Short:
                    writer.Int16LittleEndian((short)scalar);
                    break;
                case ScalarKind.UShort:
                    writer.Int16LittleEndian(unchecked((short)(ushort)scalar));
                    break;
                case ScalarKind.Int:
                    writer.Int32LittleEndian((int)scalar);
                    break;
                case ScalarKind.UInt:
                    writer.Int32LittleEndian(unchecked((int)(uint)scalar));
                    break;
                case ScalarKind.Long:
                    writer.Int64LittleEndian((long)scalar);
                    break;
                case ScalarKind.ULong:
                    writer.Int64LittleEndian(unchecked((long)(ulong)scalar));
                    break;
                case ScalarKind.Float:
                    writer.Int32LittleEndian(BitConverter.SingleToInt32Bits((float)scalar));
                    break;
                case ScalarKind.Double:
                    writer.Int64LittleEndian(BitConverter.DoubleToInt64Bits((double)scalar));
                    break;
                case ScalarKind.String:
                    var text = (string)scalar;
                    if (text.Length == 0)
                    {
                        writer.Int32LittleEndian(0);
                        break;
                    }

                    int byteCount = Encoding.UTF8.GetByteCount(text);
                    writer.Int32LittleEndian(~byteCount);
                    writer.Int32LittleEndian(text.Length);
                    writer.Utf8(text, byteCount);
                    break;
                default:
                    writer.Int64LittleEndian(((DateTime)scalar).ToBinary());
                    break;
            }
        }
    }

    public sealed class MessagePackCodec : ITableCodec
    {
        public const string CodecName = "messagepack";

        private const long UnixEpochSeconds = 62135596800L;

        public string Name => CodecName;

        public IReadOnlyList<string> Usings { get; } = new[] { "MessagePack" };

        public string RowAttribute => "[MessagePackObject(AllowPrivate = true)]";

        public string FieldAttribute(int order) => $"[Key({order})]";

        public string ConstructorAttribute => "[SerializationConstructor]";

        public string IgnoreAttribute => "[IgnoreMember]";

        public string DeserializeExpression(string rowType, string dataVariable) => $"MessagePackSerializer.Deserialize<{rowType}[]>({dataVariable})";

        public byte[] Encode(TableData table)
        {
            var writer = new ByteWriter(64 + table.Rows.Count * 24);
            TableSchema schema = table.Schema;
            int memberCount = schema.StoredMemberCount;
            WriteArrayHeader(writer, table.Rows.Count);
            foreach (RowData row in table.Rows)
            {
                WriteArrayHeader(writer, memberCount);
                for (int index = 0; index < schema.Columns.Count; index++)
                {
                    ColumnType type = schema.Columns[index].Type;
                    object value = row.Values[index];
                    if (type.IsArray)
                    {
                        var elements = (object[])value;
                        if (IsStoredAsByteArray(type))
                        {
                            WriteBinaryHeader(writer, elements.Length);
                            foreach (object element in elements)
                            {
                                writer.Byte((byte)LeafValues.Resolve(type, element).Value);
                            }
                        }
                        else
                        {
                            WriteArrayHeader(writer, elements.Length);
                            foreach (object element in elements)
                            {
                                WriteLeaf(writer, type, element);
                            }
                        }
                    }
                    else if (type.Kind == ColumnKind.Reference)
                    {
                        var reference = (RefValue)value;
                        if (type.IsOptional)
                        {
                            writer.Byte(reference.HasValue ? (byte)0xc3 : (byte)0xc2);
                        }

                        WriteLeaf(writer, type, reference.Key ?? LeafValues.Default(type));
                    }
                    else
                    {
                        WriteLeaf(writer, type, value);
                    }
                }
            }

            return writer.ToArray();
        }

        private static bool IsStoredAsByteArray(ColumnType type) =>
            (type.KeyType ?? type) is { Kind: ColumnKind.Scalar, Scalar: ScalarKind.Byte };

        private static void WriteLeaf(ByteWriter writer, ColumnType type, object value)
        {
            (ScalarKind kind, object scalar) = LeafValues.Resolve(type, value);
            switch (kind)
            {
                case ScalarKind.Bool:
                    writer.Byte((bool)scalar ? (byte)0xc3 : (byte)0xc2);
                    break;
                case ScalarKind.ULong:
                    WriteUnsigned(writer, (ulong)scalar);
                    break;
                case ScalarKind.Float:
                    writer.Byte(0xca);
                    writer.Int32BigEndian(BitConverter.SingleToInt32Bits((float)scalar));
                    break;
                case ScalarKind.Double:
                    writer.Byte(0xcb);
                    writer.Int64BigEndian(BitConverter.DoubleToInt64Bits((double)scalar));
                    break;
                case ScalarKind.String:
                    WriteString(writer, (string)scalar);
                    break;
                case ScalarKind.DateTime:
                    WriteTimestamp(writer, (DateTime)scalar);
                    break;
                default:
                    WriteSigned(writer, Scalars.ToInt64(scalar));
                    break;
            }
        }

        private static void WriteSigned(ByteWriter writer, long value)
        {
            if (value >= 0)
            {
                WriteUnsigned(writer, (ulong)value);
            }
            else if (value >= -32)
            {
                writer.Byte(unchecked((byte)value));
            }
            else if (value >= sbyte.MinValue)
            {
                writer.Byte(0xd0);
                writer.Byte(unchecked((byte)value));
            }
            else if (value >= short.MinValue)
            {
                writer.Byte(0xd1);
                writer.Int16BigEndian((short)value);
            }
            else if (value >= int.MinValue)
            {
                writer.Byte(0xd2);
                writer.Int32BigEndian((int)value);
            }
            else
            {
                writer.Byte(0xd3);
                writer.Int64BigEndian(value);
            }
        }

        private static void WriteUnsigned(ByteWriter writer, ulong value)
        {
            if (value <= 127)
            {
                writer.Byte((byte)value);
            }
            else if (value <= byte.MaxValue)
            {
                writer.Byte(0xcc);
                writer.Byte((byte)value);
            }
            else if (value <= ushort.MaxValue)
            {
                writer.Byte(0xcd);
                writer.Int16BigEndian(unchecked((short)(ushort)value));
            }
            else if (value <= uint.MaxValue)
            {
                writer.Byte(0xce);
                writer.Int32BigEndian(unchecked((int)(uint)value));
            }
            else
            {
                writer.Byte(0xcf);
                writer.Int64BigEndian(unchecked((long)value));
            }
        }

        private static void WriteString(ByteWriter writer, string text)
        {
            int byteCount = Encoding.UTF8.GetByteCount(text);
            if (byteCount <= 31)
            {
                writer.Byte((byte)(0xa0 | byteCount));
            }
            else if (byteCount <= byte.MaxValue)
            {
                writer.Byte(0xd9);
                writer.Byte((byte)byteCount);
            }
            else if (byteCount <= ushort.MaxValue)
            {
                writer.Byte(0xda);
                writer.Int16BigEndian(unchecked((short)(ushort)byteCount));
            }
            else
            {
                writer.Byte(0xdb);
                writer.Int32BigEndian(byteCount);
            }

            writer.Utf8(text, byteCount);
        }

        private static void WriteArrayHeader(ByteWriter writer, int count)
        {
            if (count <= 15)
            {
                writer.Byte((byte)(0x90 | count));
            }
            else if (count <= ushort.MaxValue)
            {
                writer.Byte(0xdc);
                writer.Int16BigEndian(unchecked((short)(ushort)count));
            }
            else
            {
                writer.Byte(0xdd);
                writer.Int32BigEndian(count);
            }
        }

        private static void WriteBinaryHeader(ByteWriter writer, int length)
        {
            if (length <= byte.MaxValue)
            {
                writer.Byte(0xc4);
                writer.Byte((byte)length);
            }
            else if (length <= ushort.MaxValue)
            {
                writer.Byte(0xc5);
                writer.Int16BigEndian(unchecked((short)(ushort)length));
            }
            else
            {
                writer.Byte(0xc6);
                writer.Int32BigEndian(length);
            }
        }

        private static void WriteTimestamp(ByteWriter writer, DateTime value)
        {
            long seconds = value.Ticks / TimeSpan.TicksPerSecond - UnixEpochSeconds;
            long nanoseconds = value.Ticks % TimeSpan.TicksPerSecond * 100;
            if (seconds >> 34 == 0)
            {
                ulong packed = unchecked((ulong)((nanoseconds << 34) | seconds));
                if ((packed & 0xffffffff00000000UL) == 0)
                {
                    writer.Byte(0xd6);
                    writer.Byte(0xff);
                    writer.Int32BigEndian(unchecked((int)(uint)packed));
                }
                else
                {
                    writer.Byte(0xd7);
                    writer.Byte(0xff);
                    writer.Int64BigEndian(unchecked((long)packed));
                }
            }
            else
            {
                writer.Byte(0xc7);
                writer.Byte(12);
                writer.Byte(0xff);
                writer.Int32BigEndian(unchecked((int)(uint)nanoseconds));
                writer.Int64BigEndian(seconds);
            }
        }
    }

    internal static class LeafValues
    {
        public static (ScalarKind Kind, object Value) Resolve(ColumnType type, object value)
        {
            if (type.Kind == ColumnKind.Reference)
            {
                return Resolve(type.KeyType!, value);
            }

            if (type.Kind == ColumnKind.Enum)
            {
                return Scalars.TryFromInt64(type.Scalar, (long)value, out object boxed)
                    ? (type.Scalar, boxed)
                    : throw new InvalidOperationException($"The value {value} does not fit the enum {type.Enum!.Name}.");
            }

            return (type.Scalar, value);
        }

        public static object Default(ColumnType type)
        {
            ColumnType key = type.KeyType ?? type;
            return key.Kind == ColumnKind.Enum ? 0L : Scalars.Default(key.Scalar);
        }
    }

    internal sealed class ByteWriter
    {
        public ByteWriter(int capacity)
        {
            _buffer = new byte[Math.Max(capacity, 16)];
        }

        private byte[] _buffer;
        private int _length;

        public void Byte(byte value)
        {
            Reserve(1)[0] = value;
        }

        public void Int16LittleEndian(short value) => BinaryPrimitives.WriteInt16LittleEndian(Reserve(2), value);

        public void Int32LittleEndian(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);

        public void Int64LittleEndian(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);

        public void Int16BigEndian(short value) => BinaryPrimitives.WriteInt16BigEndian(Reserve(2), value);

        public void Int32BigEndian(int value) => BinaryPrimitives.WriteInt32BigEndian(Reserve(4), value);

        public void Int64BigEndian(long value) => BinaryPrimitives.WriteInt64BigEndian(Reserve(8), value);

        public void Utf8(string text, int byteCount)
        {
            Encoding.UTF8.GetBytes(text.AsSpan(), Reserve(byteCount));
        }

        public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

        private Span<byte> Reserve(int count)
        {
            if (_length + count > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
            }

            Span<byte> span = _buffer.AsSpan(_length, count);
            _length += count;
            return span;
        }
    }
}
