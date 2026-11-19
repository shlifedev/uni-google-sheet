#nullable enable
using System;
using System.Collections.Generic;

namespace HolySheet.Core.Schema
{
    public enum TypeExpressionKind
    {
        /// <summary>A plain name: a value type such as int, or the name of an enum.</summary>
        Named,

        /// <summary>Enum&lt;Name&gt;.</summary>
        Enum,

        /// <summary>Ref&lt;Table&gt;: points at a row of another table by its key.</summary>
        Reference,

        /// <summary>Mapping&lt;Name&gt;: the column's text becomes the members of a generated enum whose values are the row keys. Not stored as data.</summary>
        Mapping
    }

    /// <summary>
    /// What a type cell says, before names are looked up. The grammar:
    /// <code>
    /// type     := [ "Key&lt;" | "Index&lt;" ] element [ "[]" ] [ "?" ] [ "&gt;" ]
    /// element  := name | "Enum&lt;" name "&gt;" | "Ref&lt;" table "&gt;" | "Mapping&lt;" name "&gt;"
    /// </code>
    /// <c>List&lt;T&gt;</c> is accepted as another way to write <c>T[]</c>, and <c>Enum&lt;Name[]&gt;</c> as <c>Enum&lt;Name&gt;[]</c>.
    /// </summary>
    public sealed class TypeExpression
    {
        public TypeExpressionKind Kind { get; }

        /// <summary>A value type alias, an enum name or a table name, depending on <see cref="Kind"/>.</summary>
        public string Name { get; }

        public ColumnRole Role { get; }

        public bool IsArray { get; }

        /// <summary>Written with a trailing "?": an empty cell is allowed and means "no value".</summary>
        public bool IsOptional { get; }

        private TypeExpression(TypeExpressionKind kind, string name, ColumnRole role, bool isArray, bool isOptional)
        {
            Kind = kind;
            Name = name;
            Role = role;
            IsArray = isArray;
            IsOptional = isOptional;
        }

        public static bool TryParse(string text, out TypeExpression? expression, out string reason)
        {
            expression = null;
            reason = "";
            string body = text.Trim();
            if (body.Length == 0)
            {
                reason = "The type is empty.";
                return false;
            }

            ColumnRole role = ColumnRole.None;
            if (TryUnwrap(body, "Key", out string keyInner))
            {
                role = ColumnRole.Key;
                body = keyInner;
            }
            else if (TryUnwrap(body, "Index", out string indexInner))
            {
                role = ColumnRole.Index;
                body = indexInner;
            }

            bool isOptional = body.EndsWith('?');
            if (isOptional)
            {
                body = body[..^1].TrimEnd();
            }

            bool isArray = false;
            if (TryUnwrap(body, "List", out string listInner) || TryUnwrap(body, "list", out listInner))
            {
                isArray = true;
                body = listInner;
            }
            else if (body.EndsWith("[]", StringComparison.Ordinal))
            {
                isArray = true;
                body = body[..^2].TrimEnd();
            }

            TypeExpressionKind kind = TypeExpressionKind.Named;
            if (TryUnwrap(body, "Enum", out string enumInner))
            {
                kind = TypeExpressionKind.Enum;
                body = enumInner;
                if (body.EndsWith("[]", StringComparison.Ordinal))
                {
                    isArray = true;
                    body = body[..^2].TrimEnd();
                }
            }
            else if (TryUnwrap(body, "Ref", out string refInner))
            {
                kind = TypeExpressionKind.Reference;
                body = refInner;
            }
            else if (TryUnwrap(body, "Mapping", out string mappingInner))
            {
                kind = TypeExpressionKind.Mapping;
                body = mappingInner;
            }

            if (body.Length == 0 || body.IndexOfAny(new[] { '<', '>', '[', ']', ' ', '?' }) >= 0)
            {
                reason = $"The type '{text.Trim()}' is not understood. Examples: int, string, float[], Key<int>, Index<string>, Enum<EKind>, Ref<Item>, Ref<Item>?, Mapping<EItemId>.";
                return false;
            }

            if (role != ColumnRole.None && (isArray || kind is TypeExpressionKind.Reference or TypeExpressionKind.Mapping || isOptional))
            {
                reason = $"{role}<...> takes a single required value: no arrays, no Ref, no Mapping, no '?'.";
                return false;
            }

            if (kind == TypeExpressionKind.Mapping && (isArray || isOptional))
            {
                reason = "Mapping<...> cannot be an array or optional.";
                return false;
            }

            if (isOptional && (kind != TypeExpressionKind.Reference || isArray))
            {
                reason = "'?' is only supported on a single Ref<...>. Other types use their default value (0, false, empty text) for an empty cell.";
                return false;
            }

            expression = new TypeExpression(kind, body, role, isArray, isOptional);
            return true;
        }

        // "Wrapper<inner>" gives inner.
        private static bool TryUnwrap(string text, string wrapper, out string inner)
        {
            inner = "";
            if (text.Length < wrapper.Length + 2 || !text.StartsWith(wrapper, StringComparison.Ordinal) || text[wrapper.Length] != '<' || text[^1] != '>')
            {
                return false;
            }

            inner = text.Substring(wrapper.Length + 1, text.Length - wrapper.Length - 2).Trim();
            return true;
        }
    }

    /// <summary>Looks up the names in a type expression: value types, enums, and tables for references.</summary>
    public sealed class TypeResolver
    {
        private readonly IReadOnlyDictionary<string, EnumDefinition> enums;

        public TypeResolver(IReadOnlyDictionary<string, EnumDefinition> enums)
        {
            this.enums = enums;
        }

        /// <summary>Returns the type of a table's primary key, or null when the table does not exist or has no key.</summary>
        public delegate ColumnType? KeyLookup(string tableName, out bool tableExists);

        public bool TryResolve(TypeExpression expression, KeyLookup keyLookup, out ColumnType? type, out string reason, out string hint)
        {
            type = null;
            reason = "";
            hint = "";
            switch (expression.Kind)
            {
                case TypeExpressionKind.Named:
                    if (Scalars.TryFind(expression.Name, out ScalarKind scalar))
                    {
                        type = ColumnType.OfScalar(scalar, expression.IsArray);
                        return true;
                    }

                    // A bare name that is an enum works like Enum<Name>.
                    if (enums.TryGetValue(expression.Name, out EnumDefinition? bare))
                    {
                        type = ColumnType.OfEnum(bare, expression.IsArray);
                        return true;
                    }

                    reason = $"The type '{expression.Name}' is not known.";
                    hint = "Value types: bool, byte, sbyte, short, ushort, int, uint, long, ulong, float, double, string, DateTime. An enum must be defined in an Enums sheet.";
                    return false;
                case TypeExpressionKind.Enum:
                    if (enums.TryGetValue(expression.Name, out EnumDefinition? definition))
                    {
                        type = ColumnType.OfEnum(definition, expression.IsArray);
                        return true;
                    }

                    reason = $"There is no enum '{expression.Name}'.";
                    hint = "Define it in a sheet named Enums (or Something.Enums), or with a Mapping<...> column.";
                    return false;
                case TypeExpressionKind.Reference:
                    ColumnType? keyType = keyLookup(expression.Name, out bool tableExists);
                    if (keyType == null)
                    {
                        reason = tableExists ? $"The table '{expression.Name}' has no key to refer to." : $"There is no table '{expression.Name}' to refer to.";
                        hint = tableExists ? $"Mark a column of '{expression.Name}' as Key<...>." : "Check the spelling; it must match the sheet name exactly.";
                        return false;
                    }

                    type = ColumnType.OfReference(expression.Name, keyType, expression.IsArray, expression.IsOptional);
                    return true;
                default:
                    reason = "A Mapping<...> column holds no data.";
                    return false;
            }
        }
    }
}
