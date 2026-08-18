using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using ArkBrowser.Core.Serialization;

namespace ArkBrowser.UI;

internal sealed record ThumbnailJobInput(Texture2DInfo Texture, byte[] CompressedData);
internal sealed record ThumbnailRequest(string Key, Func<ThumbnailJobInput?> LoadJob);

internal sealed class ThumbnailService : IDisposable
{
    private const int MaxThumbnailDimension = 256;

    private readonly ConcurrentDictionary<string, BitmapSource> cache = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetimeCts = new();
    private CancellationTokenSource? batchCts;
    private int generation;
    private bool disposed;

    public event Action<int, int>? ProgressChanged;
    public event Action<string, BitmapSource>? ThumbnailReady;

    public bool TryGet(string key, out BitmapSource? thumbnail)
    {
        return cache.TryGetValue(key, out thumbnail);
    }

    public void Enqueue(IReadOnlyList<ThumbnailRequest> requests)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (requests.Count == 0)
        {
            ProgressChanged?.Invoke(0, 0);
            return;
        }

        CancellationToken token;
        int currentGeneration;
        lock (gate)
        {
            batchCts?.Cancel();
            batchCts?.Dispose();
            batchCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts.Token);
            token = batchCts.Token;
            currentGeneration = ++generation;
        }

        _ = RunBatchAsync(requests, token, currentGeneration);
    }

    public void Clear()
    {
        lock (gate)
        {
            batchCts?.Cancel();
            batchCts?.Dispose();
            batchCts = null;
            generation++;
        }

        cache.Clear();
        ProgressChanged?.Invoke(0, 0);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lock (gate)
        {
            batchCts?.Cancel();
            batchCts?.Dispose();
            batchCts = null;
        }

        lifetimeCts.Cancel();
        lifetimeCts.Dispose();
    }

    private async Task RunBatchAsync(IReadOnlyList<ThumbnailRequest> requests, CancellationToken token, int currentGeneration)
    {
        int total = requests.Count;
        int completed = 0;
        if (IsCurrent(currentGeneration))
        {
            ProgressChanged?.Invoke(0, total);
        }

        ParallelOptions options = new()
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
        };

        try
        {
            await Parallel.ForEachAsync(requests, options, (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                BitmapSource? thumbnail = null;
                try
                {
                    ThumbnailJobInput? input = request.LoadJob();
                    if (input is not null)
                    {
                        byte[] rgba = Texture2DDecoder.DecodeToRgba32(input.Texture, input.CompressedData);
                        (byte[] thumbnailRgba, int thumbnailWidth, int thumbnailHeight) = DownscaleRgba32(
                            rgba,
                            input.Texture.Width,
                            input.Texture.Height,
                            MaxThumbnailDimension);
                        thumbnail = MainWindow.CreateBitmapSource(thumbnailRgba, thumbnailWidth, thumbnailHeight);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    thumbnail = null;
                }

                if (thumbnail is not null)
                {
                    cache[request.Key] = thumbnail;
                    ThumbnailReady?.Invoke(request.Key, thumbnail);
                }

                int done = Interlocked.Increment(ref completed);
                if (IsCurrent(currentGeneration))
                {
                    ProgressChanged?.Invoke(done, total);
                }

                return ValueTask.CompletedTask;
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool IsCurrent(int expectedGeneration)
    {
        return Volatile.Read(ref generation) == expectedGeneration;
    }

    private static (byte[] Pixels, int Width, int Height) DownscaleRgba32(
        byte[] rgba,
        int sourceWidth,
        int sourceHeight,
        int maxDimension)
    {
        if (sourceWidth <= maxDimension && sourceHeight <= maxDimension)
        {
            return (rgba, sourceWidth, sourceHeight);
        }

        double scale = Math.Min(
            (double)maxDimension / sourceWidth,
            (double)maxDimension / sourceHeight);
        int targetWidth = Math.Max(1, (int)Math.Floor(sourceWidth * scale));
        int targetHeight = Math.Max(1, (int)Math.Floor(sourceHeight * scale));
        byte[] output = new byte[targetWidth * targetHeight * 4];

        for (int y = 0; y < targetHeight; y++)
        {
            double sourceY = (y + 0.5) * sourceHeight / targetHeight - 0.5;
            int y0 = Math.Max(0, (int)Math.Floor(sourceY));
            int y1 = Math.Min(sourceHeight - 1, y0 + 1);
            double weightY = sourceY - Math.Floor(sourceY);

            for (int x = 0; x < targetWidth; x++)
            {
                double sourceX = (x + 0.5) * sourceWidth / targetWidth - 0.5;
                int x0 = Math.Max(0, (int)Math.Floor(sourceX));
                int x1 = Math.Min(sourceWidth - 1, x0 + 1);
                double weightX = sourceX - Math.Floor(sourceX);

                int targetOffset = (y * targetWidth + x) * 4;
                int p00 = (y0 * sourceWidth + x0) * 4;
                int p10 = (y0 * sourceWidth + x1) * 4;
                int p01 = (y1 * sourceWidth + x0) * 4;
                int p11 = (y1 * sourceWidth + x1) * 4;

                for (int channel = 0; channel < 4; channel++)
                {
                    double top = rgba[p00 + channel] * (1.0 - weightX) + rgba[p10 + channel] * weightX;
                    double bottom = rgba[p01 + channel] * (1.0 - weightX) + rgba[p11 + channel] * weightX;
                    double value = top * (1.0 - weightY) + bottom * weightY;
                    output[targetOffset + channel] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
                }
            }
        }

        return (output, targetWidth, targetHeight);
    }
}
