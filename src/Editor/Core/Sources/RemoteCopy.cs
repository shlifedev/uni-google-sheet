#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace UGS.Core.Sources
{
    /// <summary>
    /// The tabs of a Google file as last downloaded, with the Drive version they were downloaded at. When Drive reports the
    /// same version again, the file has not changed and the tabs are read from here instead of from Google, which costs one
    /// cheap Drive request instead of Sheets API requests. Kept under the sheet cache folder, one folder per file id.
    /// </summary>
    internal static class RemoteCopy
    {
        private const string IndexFileName = "copy.index";
        private const string Header = "ugs-remote-copy 1";
        private static readonly UTF8Encoding Utf8 = new(false);

        public sealed record Copy(string Title, IReadOnlyList<(string Name, string Text)> Tabs);

        private static string FolderOf(string root, string fileId) => Path.Combine(root, GoogleSheetsSource.SafeFileName(fileId));

        /// <summary>The copy made at exactly this version, or null.</summary>
        public static Copy? TryLoad(string? root, string fileId, long version)
        {
            if (root == null)
            {
                return null;
            }

            string folder = FolderOf(root, fileId);
            try
            {
                string indexPath = Path.Combine(folder, IndexFileName);
                if (!File.Exists(indexPath))
                {
                    return null;
                }

                string[] lines = File.ReadAllText(indexPath, Utf8).Split('\n');
                if (lines.Length < 3 || lines[0] != Header || lines[1] != "version " + version || !lines[2].StartsWith("title\t", StringComparison.Ordinal))
                {
                    return null;
                }

                var tabs = new List<(string Name, string Text)>();
                foreach (string line in lines.Skip(3))
                {
                    string[] fields = line.Split('\t');
                    if (fields.Length == 3 && fields[0] == "tab")
                    {
                        tabs.Add((fields[2], File.ReadAllText(Path.Combine(folder, fields[1]), Utf8)));
                    }
                }

                return new Copy(lines[2].Substring("title\t".Length), tabs);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Replaces the copy. Only a complete download is saved: a partial one would mix versions.</summary>
        public static void Save(string? root, string fileId, long version, string title, IReadOnlyList<(string Name, string Text)> tabs)
        {
            if (root == null)
            {
                return;
            }

            string folder = FolderOf(root, fileId);
            try
            {
                Invalidate(root, fileId);
                Directory.CreateDirectory(folder);
                foreach (string old in Directory.GetFiles(folder, "*.csv"))
                {
                    File.Delete(old);
                }

                var index = new StringBuilder();
                index.Append(Header).Append('\n').Append("version ").Append(version).Append('\n').Append("title\t").Append(OneLine(title)).Append('\n');
                for (int position = 0; position < tabs.Count; position++)
                {
                    string file = position + ".csv";
                    File.WriteAllText(Path.Combine(folder, file), tabs[position].Text, Utf8);
                    index.Append("tab\t").Append(file).Append('\t').Append(OneLine(tabs[position].Name)).Append('\n');
                }

                // Written last: an interrupted save leaves no index, so nothing half-written is ever read.
                File.WriteAllText(Path.Combine(folder, IndexFileName), index.ToString(), Utf8);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Invalidate(root, fileId);
            }
        }

        /// <summary>Forgets the copy, so the next build downloads the file again.</summary>
        public static void Invalidate(string? root, string fileId)
        {
            if (root == null)
            {
                return;
            }

            try
            {
                File.Delete(Path.Combine(FolderOf(root, fileId), IndexFileName));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        private static string OneLine(string text) => text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }
}
