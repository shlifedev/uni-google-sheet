#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HolySheet.Core.Schema
{
    public static class Identifier
    {
        public const string Rule = "Start with a letter or underscore, then letters, digits or underscores; no spaces, and not a C# keyword.";

        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
            "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
            "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long",
            "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
            "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
            "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void",
            "volatile", "while"
        };

        public static bool IsValid(string name)
        {
            if (string.IsNullOrEmpty(name) || Keywords.Contains(name))
            {
                return false;
            }

            if (!(char.IsLetter(name[0]) || name[0] == '_'))
            {
                return false;
            }

            foreach (char c in name)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_'))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public enum ScalarKind
    {
        Bool,
        SByte,
        Byte,
        Short,
        UShort,
        Int,
        UInt,
        Long,
        ULong,
        Float,
        Double,
        String,
        DateTime
    }

    public static class Scalars
    {
        private static readonly Dictionary<string, ScalarKind> ByName = new(StringComparer.OrdinalIgnoreCase)
        {
            ["bool"] = ScalarKind.Bool, ["boolean"] = ScalarKind.Bool,
            ["sbyte"] = ScalarKind.SByte, ["byte"] = ScalarKind.Byte,
            ["short"] = ScalarKind.Short, ["ushort"] = ScalarKind.UShort,
            ["int"] = ScalarKind.Int, ["uint"] = ScalarKind.UInt,
            ["long"] = ScalarKind.Long, ["ulong"] = ScalarKind.ULong,
            ["float"] = ScalarKind.Float, ["double"] = ScalarKind.Double,
            ["string"] = ScalarKind.String, ["datetime"] = ScalarKind.DateTime
        };

        private static readonly string[] DateFormats =
        {
            "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.FFFFFFFZ",
            "yyyy/MM/dd", "yyyy/MM/dd HH:mm:ss", "yyyy.MM.dd"
        };

        public static bool TryFind(string name, out ScalarKind kind) => ByName.TryGetValue(name, out kind);

        public static string CSharpName(ScalarKind kind) => kind switch
        {
            ScalarKind.Bool => "bool",
            ScalarKind.SByte => "sbyte",
            ScalarKind.Byte => "byte",
            ScalarKind.Short => "short",
            ScalarKind.UShort => "ushort",
            ScalarKind.Int => "int",
            ScalarKind.UInt => "uint",
            ScalarKind.Long => "long",
            ScalarKind.ULong => "ulong",
            ScalarKind.Float => "float",
            ScalarKind.Double => "double",
            ScalarKind.String => "string",
            _ => "System.DateTime"
        };

        public static string CanonicalName(ScalarKind kind) => kind == ScalarKind.DateTime ? "DateTime" : CSharpName(kind);

        public static bool IsInteger(ScalarKind kind) => kind is >= ScalarKind.SByte and <= ScalarKind.ULong;

        public static bool CanBeKey(ScalarKind kind) => IsInteger(kind) || kind == ScalarKind.String;

        public static object Default(ScalarKind kind) => kind switch
        {
            ScalarKind.Bool => false,
            ScalarKind.SByte => (sbyte)0,
            ScalarKind.Byte => (byte)0,
            ScalarKind.Short => (short)0,
            ScalarKind.UShort => (ushort)0,
            ScalarKind.Int => 0,
            ScalarKind.UInt => 0u,
            ScalarKind.Long => 0L,
            ScalarKind.ULong => 0ul,
            ScalarKind.Float => 0f,
            ScalarKind.Double => 0d,
            ScalarKind.String => "",
            _ => new DateTime(0, DateTimeKind.Utc)
        };

        public static string Example(ScalarKind kind) => kind switch
        {
            ScalarKind.Bool => "true",
            ScalarKind.Float or ScalarKind.Double => "1.5",
            ScalarKind.String => "text",
            ScalarKind.DateTime => "2026-01-31 13:30:00",
            _ => "10"
        };

        public static bool TryParse(ScalarKind kind, string text, out object value, out string reason)
        {
            reason = "";
            if (kind == ScalarKind.String)
            {
                value = text;
                return true;
            }

            string trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                value = Default(kind);
                return true;
            }

            const NumberStyles integer = NumberStyles.AllowLeadingSign;
            const NumberStyles real = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
            CultureInfo invariant = CultureInfo.InvariantCulture;
            bool ok;
            switch (kind)
            {
                case ScalarKind.Bool:
                    ok = TryParseBool(trimmed, out bool boolean);
                    value = boolean;
                    reason = ok ? "" : "is not a boolean. Use true, false, 1 or 0";
                    return ok;
                case ScalarKind.SByte:
                    ok = sbyte.TryParse(trimmed, integer, invariant, out sbyte i8);
                    value = i8;
                    break;
                case ScalarKind.Byte:
                    ok = byte.TryParse(trimmed, integer, invariant, out byte u8);
                    value = u8;
                    break;
                case ScalarKind.Short:
                    ok = short.TryParse(trimmed, integer, invariant, out short i16);
                    value = i16;
                    break;
                case ScalarKind.UShort:
                    ok = ushort.TryParse(trimmed, integer, invariant, out ushort u16);
                    value = u16;
                    break;
                case ScalarKind.Int:
                    ok = int.TryParse(trimmed, integer, invariant, out int i32);
                    value = i32;
                    break;
                case ScalarKind.UInt:
                    ok = uint.TryParse(trimmed, integer, invariant, out uint u32);
                    value = u32;
                    break;
                case ScalarKind.Long:
                    ok = long.TryParse(trimmed, integer, invariant, out long i64);
                    value = i64;
                    break;
                case ScalarKind.ULong:
                    ok = ulong.TryParse(trimmed, integer, invariant, out ulong u64);
                    value = u64;
                    break;
                case ScalarKind.Float:
                    ok = float.TryParse(trimmed, real, invariant, out float f32) && !float.IsInfinity(f32) && !float.IsNaN(f32);
                    value = f32;
                    reason = ok ? "" : "is not a number. Write it like 1.5, without thousands separators";
                    return ok;
                case ScalarKind.Double:
                    ok = double.TryParse(trimmed, real, invariant, out double f64) && !double.IsInfinity(f64) && !double.IsNaN(f64);
                    value = f64;
                    reason = ok ? "" : "is not a number. Write it like 1.5, without thousands separators";
                    return ok;
                default:
                    ok = DateTime.TryParseExact(trimmed, DateFormats, invariant, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime date);
                    value = DateTime.SpecifyKind(date, DateTimeKind.Utc);
                    reason = ok ? "" : "is not a date. Write it like 2026-01-31 or 2026-01-31 13:30:00 (UTC)";
                    return ok;
            }

            if (!ok)
            {
                reason = decimal.TryParse(trimmed, integer, invariant, out _)
                    ? $"does not fit in {CSharpName(kind)} ({Range(kind)})"
                    : $"is not a whole number. Write it like 10, without decimals or thousands separators";
            }

            return ok;
        }

        private static bool TryParseBool(string text, out bool value)
        {
            if (text == "1" || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }

            value = false;
            return text == "0" || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase);
        }

        private static string Range(ScalarKind kind) => kind switch
        {
            ScalarKind.SByte => "-128 to 127",
            ScalarKind.Byte => "0 to 255",
            ScalarKind.Short => "-32768 to 32767",
            ScalarKind.UShort => "0 to 65535",
            ScalarKind.Int => "-2147483648 to 2147483647",
            ScalarKind.UInt => "0 to 4294967295",
            ScalarKind.Long => "about ±9.2e18",
            _ => "0 to about 1.8e19"
        };

        public static long ToInt64(object value) => value switch
        {
            sbyte v => v,
            byte v => v,
            short v => v,
            ushort v => v,
            int v => v,
            uint v => v,
            long v => v,
            ulong v => unchecked((long)v),
            _ => throw new InvalidOperationException($"{value.GetType().Name} is not an integer.")
        };

        public static bool TryFromInt64(ScalarKind kind, long value, out object boxed)
        {
            switch (kind)
            {
                case ScalarKind.SByte when value is >= sbyte.MinValue and <= sbyte.MaxValue:
                    boxed = (sbyte)value;
                    return true;
                case ScalarKind.Byte when value is >= byte.MinValue and <= byte.MaxValue:
                    boxed = (byte)value;
                    return true;
                case ScalarKind.Short when value is >= short.MinValue and <= short.MaxValue:
                    boxed = (short)value;
                    return true;
                case ScalarKind.UShort when value is >= ushort.MinValue and <= ushort.MaxValue:
                    boxed = (ushort)value;
                    return true;
                case ScalarKind.Int when value is >= int.MinValue and <= int.MaxValue:
                    boxed = (int)value;
                    return true;
                case ScalarKind.UInt when value is >= uint.MinValue and <= uint.MaxValue:
                    boxed = (uint)value;
                    return true;
                case ScalarKind.Long:
                    boxed = value;
                    return true;
                case ScalarKind.ULong when value >= 0:
                    boxed = (ulong)value;
                    return true;
                default:
                    boxed = 0;
                    return false;
            }
        }
    }

    public sealed record EnumMember(string Name, long Value, string Comment = "");

    public sealed class EnumDefinition
    {
        public EnumDefinition(string name, ScalarKind underlying, bool isFlags, IReadOnlyList<EnumMember> members, string comment, string location)
        {
            _byName = members.GroupBy(member => member.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            Name = name;
            Underlying = underlying;
            IsFlags = isFlags;
            Members = members;
            Comment = comment;
            Location = location;
        }

        private readonly Dictionary<string, EnumMember> _byName;

        public string Name { get; }

        public ScalarKind Underlying { get; }

        public bool IsFlags { get; }

        public IReadOnlyList<EnumMember> Members { get; }

        public string Comment { get; }

        public string Location { get; }

        public EnumMember? Find(string memberName) => _byName.TryGetValue(memberName, out EnumMember? member) ? member : null;

        public EnumMember? FindByValue(long value) => Members.FirstOrDefault(member => member.Value == value);
    }

    public enum ColumnKind
    {
        Scalar,
        Enum,
        Reference
    }

    public enum ColumnRole
    {
        None,

        Key,

        Index
    }

    public sealed record RefValue(bool HasValue, object? Key)
    {
        public static RefValue None { get; } = new(false, null);
    }

    public sealed class ColumnType
    {
        public ColumnKind Kind { get; }

        public ScalarKind Scalar { get; }

        public EnumDefinition? Enum { get; }

        public string? TargetTable { get; }

        public ColumnType? KeyType { get; }

        public bool IsArray { get; }

        public bool IsOptional { get; }

        private ColumnType(ColumnKind kind, ScalarKind scalar, EnumDefinition? enumDefinition, string? targetTable, ColumnType? keyType, bool isArray, bool isOptional)
        {
            Kind = kind;
            Scalar = scalar;
            Enum = enumDefinition;
            TargetTable = targetTable;
            KeyType = keyType;
            IsArray = isArray;
            IsOptional = isOptional;
        }

        public static ColumnType OfScalar(ScalarKind kind, bool isArray = false) => new(ColumnKind.Scalar, kind, null, null, null, isArray, false);

        public static ColumnType OfEnum(EnumDefinition definition, bool isArray = false) => new(ColumnKind.Enum, definition.Underlying, definition, null, null, isArray, false);

        public static ColumnType OfReference(string targetTable, ColumnType keyType, bool isArray = false, bool isOptional = false) =>
            new(ColumnKind.Reference, keyType.Scalar, keyType.Enum, targetTable, keyType, isArray, isOptional);

        public ColumnType AsArray() => new(Kind, Scalar, Enum, TargetTable, KeyType, true, false);

        public ColumnType ElementType => IsArray ? new ColumnType(Kind, Scalar, Enum, TargetTable, KeyType, false, false) : this;

        public bool CanBeKey => !IsArray && Kind != ColumnKind.Reference && (Kind == ColumnKind.Enum || Scalars.CanBeKey(Scalar));

        public string CanonicalName
        {
            get
            {
                string element = Kind switch
                {
                    ColumnKind.Enum => $"Enum<{Enum!.Name}>",
                    ColumnKind.Reference => $"Ref<{TargetTable}>",
                    _ => Scalars.CanonicalName(Scalar)
                };
                return element + (IsArray ? "[]" : "") + (IsOptional ? "?" : "");
            }
        }

        public string CSharpValueType => Kind switch
        {
            ColumnKind.Enum => Enum!.Name,
            ColumnKind.Reference => KeyType!.CSharpValueType,
            _ => Scalars.CSharpName(Scalar)
        };

        public string Example => Kind switch
        {
            ColumnKind.Enum => Enum!.Members.Count > 0 ? Enum.Members[0].Name : "Member",
            ColumnKind.Reference => KeyType!.Example,
            _ => Scalars.Example(Scalar)
        };
    }
}
