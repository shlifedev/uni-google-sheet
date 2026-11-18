#nullable enable
#if UNITY_5_3_OR_NEWER
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace UGS
{
    /// <summary>
    /// Reads table data from a folder under StreamingAssets.
    /// On desktop, iOS and consoles the files are plain files, so both <see cref="Read"/> and <see cref="ReadAsync"/> work.
    /// On Android and WebGL they sit inside the package or behind a URL: only <see cref="ReadAsync"/> works there.
    /// </summary>
    public sealed class StreamingAssetsTableSource : TableSource
    {
        private readonly string _root;

        public override string Description => _root;

        /// <param name="folder">Folder relative to StreamingAssets, for example "Tables".</param>
        public StreamingAssetsTableSource(string folder)
        {
            _root = string.IsNullOrEmpty(folder) ? Application.streamingAssetsPath : Application.streamingAssetsPath + "/" + folder;
        }

        /// <summary>True when the files can be opened directly, which is what the synchronous load and the watcher need.</summary>
        public bool IsFileSystem => !_root.Contains("://");

        /// <summary>The absolute directory, usable with a <see cref="TableWatcher{T}"/> when <see cref="IsFileSystem"/> is true.</summary>
        public string Directory => _root;

        public override byte[]? Read(string fileName)
        {
            if (!IsFileSystem)
            {
                throw new NotSupportedException("StreamingAssets cannot be read synchronously on this platform. Use LoadAsync instead of Load.");
            }

            string path = Path.Combine(_root, fileName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        public override async ValueTask<byte[]?> ReadAsync(string fileName, CancellationToken cancellationToken)
        {
            if (IsFileSystem)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Read(fileName);
            }

            using (UnityWebRequest request = UnityWebRequest.Get(_root + "/" + fileName))
            {
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    return null;
                }

                return request.downloadHandler.data;
            }
        }
    }

    /// <summary>
    /// Reads table data from a Resources folder. Each file is looked up as a TextAsset by its name without the last extension:
    /// "Item.bytes" is <c>Resources.Load("folder/Item")</c> and "tables.manifest.txt" is <c>Resources.Load("folder/tables.manifest")</c>.
    /// Must be used on the main thread, so it cannot be combined with <see cref="TableLoadOptions.Parallel"/>.
    /// </summary>
    public sealed class ResourcesTableSource : TableSource
    {
        private readonly string _folder;

        public override string Description => "Resources/" + _folder;

        public ResourcesTableSource(string folder)
        {
            _folder = (folder ?? "").Trim('/');
        }

        public override byte[]? Read(string fileName)
        {
            string name = Path.GetFileNameWithoutExtension(fileName);
            string path = _folder.Length == 0 ? name : _folder + "/" + name;
            TextAsset asset = Resources.Load<TextAsset>(path);
            if (asset == null)
            {
                return null;
            }

            byte[] bytes = asset.bytes;
            Resources.UnloadAsset(asset);
            return bytes;
        }
    }
}
#endif
