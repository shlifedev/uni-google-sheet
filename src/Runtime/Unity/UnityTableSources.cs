#nullable enable
#if UNITY_5_3_OR_NEWER
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace HolySheet
{
    public sealed class StreamingAssetsTableSource : TableSource
    {
        private readonly string _root;
        private readonly SynchronizationContext? _mainThread;

        public override string Description => _root;

        public StreamingAssetsTableSource(string folder)
        {
            _root = string.IsNullOrEmpty(folder) ? Application.streamingAssetsPath : Application.streamingAssetsPath + "/" + folder;
            _mainThread = SynchronizationContext.Current;
        }

        public bool IsFileSystem => !_root.Contains("://");

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

        public override ValueTask<byte[]?> ReadAsync(string fileName, CancellationToken cancellationToken)
        {
            if (IsFileSystem)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<byte[]?>(Read(fileName));
            }

            if (_mainThread == null || SynchronizationContext.Current == _mainThread)
            {
                return new ValueTask<byte[]?>(DownloadAsync(fileName, cancellationToken));
            }

            var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mainThread.Post(_ => Forward(DownloadAsync(fileName, cancellationToken), completion, cancellationToken), null);
            return new ValueTask<byte[]?>(completion.Task);
        }

        private static async void Forward(Task<byte[]?> download, TaskCompletionSource<byte[]?> completion, CancellationToken cancellationToken)
        {
            try
            {
                completion.TrySetResult(await download);
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private async Task<byte[]?> DownloadAsync(string fileName, CancellationToken cancellationToken)
        {
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
