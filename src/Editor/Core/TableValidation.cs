#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HolySheet.Core.Config;
using HolySheet.Core.Schema;

namespace HolySheet.Core
{
    public interface ITableValidator
    {
        void Validate(ValidationContext context);
    }

    public sealed class ValidationContext
    {
        private readonly Dictionary<string, ValidationTable> _tables;
        private readonly DiagnosticBag _diagnostics;

        internal ValidationContext(IEnumerable<TableData> tables, DiagnosticBag diagnostics)
        {
            _tables = tables.ToDictionary(t => t.Schema.Name, t => new ValidationTable(t), StringComparer.Ordinal);
            _diagnostics = diagnostics;
        }

        public ValidationTable Table(string name) => _tables.TryGetValue(name, out var table) ? table
            : throw new ArgumentException($"There is no table '{name}'.");

        public void Error(ValidationRow row, string field, string message, string hint = "") =>
            _diagnostics.AddError(row.Cell(field), message, hint);

        public void Warning(ValidationRow row, string field, string message, string hint = "") =>
            _diagnostics.AddWarning(row.Cell(field), message, hint);
    }

    public sealed class ValidationTable
    {
        private readonly TableData _data;
        private readonly Dictionary<string, int> _fields;

        internal ValidationTable(TableData data)
        {
            _data = data;
            _fields = data.Schema.Columns.Select((column, index) => (column.Field, index))
                .ToDictionary(entry => entry.Field, entry => entry.index, StringComparer.Ordinal);
        }

        public IEnumerable<ValidationRow> Rows => _data.Rows.Select(row => new ValidationRow(this, row));

        internal int Index(string field) => _fields.TryGetValue(field, out int index) ? index
            : throw new ArgumentException($"Table '{_data.Schema.Name}' has no field '{field}'.");
    }

    public readonly struct ValidationRow
    {
        private readonly ValidationTable _table;
        private readonly RowData _row;

        internal ValidationRow(ValidationTable table, RowData row)
        {
            _table = table;
            _row = row;
        }

        public T Get<T>(string field)
        {
            object value = _row.Values[_table.Index(field)];
            return (T)(value is Array array ? array.Clone() : value);
        }

        public T[] GetArray<T>(string field) => Array.ConvertAll((object[])_row.Values[_table.Index(field)], value => (T)value);

        internal Cell Cell(string field) => _row.Cells[_table.Index(field)];
    }

    internal static class TableValidation
    {
        internal static void Run(HolySheetConfig config, TableBuildOptions options, TableData[] tables, DiagnosticBag diagnostics, CancellationToken token)
        {
            var types = new HashSet<Type>(options.ValidatorTypes ?? Array.Empty<Type>());
            foreach (string path in config.ValidatorAssemblies.Distinct(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string file = config.Resolve(path);
                    Assembly assembly = options.LoadValidatorAssembly != null ? options.LoadValidatorAssembly(file) : Assembly.LoadFrom(file);
                    var rules = assembly.GetExportedTypes().Where(IsValidator).ToList();
                    if (rules.Count == 0) throw new InvalidDataException("There are no public concrete ITableValidator classes.");
                    types.UnionWith(rules);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    diagnostics.AddError("config", $"Could not load validators from '{path}': {ex.GetBaseException().Message}");
                }
            }

            if (types.Count == 0) return;
            var context = new ValidationContext(tables, diagnostics);
            foreach (Type type in types.OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var validator = (ITableValidator)Activator.CreateInstance(type)!;
                    validator.Validate(context);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
                {
                    diagnostics.AddError("validator", $"{type.FullName}: {ex.GetBaseException().Message}");
                }
            }
        }

        internal static bool IsValidator(Type type) =>
            type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters && typeof(ITableValidator).IsAssignableFrom(type);
    }
}
