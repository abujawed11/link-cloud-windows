using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LinkCloudPlayer.Models;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace LinkCloudPlayer.Services;

public class MediaInfo
{
    public long DurationMs { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public List<string> AudioLanguages { get; set; } = [];
    public string? ThumbnailPath { get; set; }
    public bool Failed { get; set; }
    public DateTime GeneratedAt { get; set; }
    public int Version { get; set; }
}

/// <summary>
/// Builds a thumbnail (a frame ~10% into the video) plus duration and audio
/// languages for each library file, using a private, windowless LibVLC
/// instance that decodes into memory. Results are cached on disk under
/// %LocalAppData%\LinkCloudPlayer\cache, so each file is only processed once.
/// Files are processed one at a time in the background; pause it while the
/// player is open so it doesn't compete for bandwidth.
/// </summary>
public sealed class MediaInfoService : IDisposable
{
    private const int CurrentVersion = 1;
    private const int ThumbWidth = 640;
    private static readonly TimeSpan RetryFailedAfter = TimeSpan.FromHours(12);

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LinkCloudPlayer",
        "cache");

    private readonly Func<int, string> _streamUrl;
    private readonly Dispatcher _dispatcher;
    private readonly Channel<FileItem> _queue = Channel.CreateUnbounded<FileItem>();
    private readonly HashSet<int> _queued = [];
    private readonly CancellationTokenSource _cts = new();
    private LibVLC? _libVlc;

    /// <summary>Raised on the UI thread when a file's info is ready.</summary>
    public event Action<int, MediaInfo>? InfoReady;

    public bool Paused { get; set; }

    public MediaInfoService(Func<int, string> streamUrl)
    {
        _streamUrl = streamUrl;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Directory.CreateDirectory(CacheDir);
        _ = Task.Run(WorkerAsync);
    }

    private static string Key(FileItem f) => $"{f.Id}_{f.SizeBytes}";
    private static string MetaPath(FileItem f) => Path.Combine(CacheDir, Key(f) + ".json");
    private static string ThumbPath(FileItem f) => Path.Combine(CacheDir, Key(f) + ".jpg");

    public MediaInfo? GetCached(FileItem file)
    {
        try
        {
            var path = MetaPath(file);
            if (!File.Exists(path)) return null;
            var info = JsonSerializer.Deserialize<MediaInfo>(File.ReadAllText(path));
            if (info is null || info.Version != CurrentVersion) return null;
            if (info.ThumbnailPath is not null && !File.Exists(info.ThumbnailPath)) return null;
            return info;
        }
        catch
        {
            return null;
        }
    }

    public static bool NeedsGeneration(MediaInfo? info) =>
        info is null || (info.Failed && DateTime.UtcNow - info.GeneratedAt > RetryFailedAfter);

    public void Enqueue(FileItem file)
    {
        lock (_queued)
        {
            if (!_queued.Add(file.Id)) return;
        }
        _queue.Writer.TryWrite(file);
    }

    private async Task WorkerAsync()
    {
        var token = _cts.Token;
        try
        {
            await foreach (var file in _queue.Reader.ReadAllAsync(token))
            {
                while (Paused) await Task.Delay(500, token);

                MediaInfo info;
                try
                {
                    info = await GenerateAsync(file, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    App.LogToFile($"MediaInfoService({file.Id})", ex);
                    info = new MediaInfo { Failed = true };
                }

                info.GeneratedAt = DateTime.UtcNow;
                info.Version = CurrentVersion;
                try { File.WriteAllText(MetaPath(file), JsonSerializer.Serialize(info)); } catch { /* cache is best-effort */ }

                lock (_queued) _queued.Remove(file.Id);
                _ = _dispatcher.BeginInvoke(() => InfoReady?.Invoke(file.Id, info));
            }
        }
        catch (OperationCanceledException) { /* disposed */ }
    }

    // IMPORTANT (libvlc 3 bug, reproduced): after Media.Parse() on a network
    // stream, disposing that Media and starting playback of a NEW Media for
    // the same URL crashes libvlc natively (access violation - kills the
    // whole app, uncatchable). So each file is parsed ONCE and that very same
    // Media object is then played for the frame grab; the fallback frame is
    // taken by seeking the same player, never by opening the URL again.
    private async Task<MediaInfo> GenerateAsync(FileItem file, CancellationToken token)
    {
        _libVlc ??= new LibVLC(
            "--no-audio", "--no-spu", "--no-osd", "--no-video-title-show",
            "--avcodec-hw=none", "--quiet");

        var info = new MediaInfo();
        using var media = new Media(_libVlc, _streamUrl(file.Id), FromType.FromLocation);

        var status = await media.Parse(MediaParseOptions.ParseNetwork, 20_000, token);
        if (status != MediaParsedStatus.Done) throw new InvalidOperationException($"Parse status: {status}");

        int width = 0, height = 0;
        info.DurationMs = media.Duration;
        foreach (var track in media.Tracks)
        {
            if (track.TrackType == TrackType.Video && width == 0)
            {
                var v = track.Data.Video;
                width = (int)v.Width;
                height = (int)v.Height;
                if (v.SarNum > 0 && v.SarDen > 0 && v.SarNum != v.SarDen)
                    width = (int)(width * (double)v.SarNum / v.SarDen);
            }
            else if (track.TrackType == TrackType.Audio)
            {
                var lang = LanguageNames.FromCode(track.Language);
                if (lang is not null && !info.AudioLanguages.Contains(lang)) info.AudioLanguages.Add(lang);
            }
        }
        info.Width = width;
        info.Height = height;

        if (width <= 0 || height <= 0 || info.DurationMs <= 0) return info; // audio file or unknown length

        var thumbW = ThumbWidth;
        var thumbH = Math.Max(16, (int)Math.Round(thumbW * (double)height / width / 16) * 16); // multiple of 16 keeps VLC's scaler happy
        var frame = await GrabFrameAsync(media, info.DurationMs, thumbW, thumbH, token);
        if (frame is not null)
        {
            var path = ThumbPath(file);
            SaveJpeg(frame, thumbW, thumbH, AlignUp(thumbW * 4, 32), path);
            info.ThumbnailPath = path;
        }
        return info;
    }

    // Plays the (already parsed) media into an in-memory RV32 buffer - no
    // window - starting 10% in. If that frame is nearly black (fade, title
    // card) it seeks the same player to 30% and grabs again.
    private static async Task<byte[]?> GrabFrameAsync(Media media, long durationMs, int w, int h, CancellationToken token)
    {
        // VLC's scaler writes in blocks of rows and may round the pitch up;
        // align and pad the buffer so it can never be overrun. Only the
        // first pitch*h bytes are read back.
        var pitch = AlignUp(w * 4, 32);
        var size = pitch * h;
        var allocSize = pitch * (AlignUp(h, 32) + 64);
        var buffer = Marshal.AllocHGlobal(allocSize);

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0;

        MediaPlayer.LibVLCVideoLockCb lockCb = (_, planes) =>
        {
            Marshal.WriteIntPtr(planes, buffer);
            return IntPtr.Zero;
        };
        MediaPlayer.LibVLCVideoDisplayCb displayCb = (_, _) =>
        {
            // Skip the first few frames - right after a seek they can still
            // be partially decoded.
            if (Interlocked.Increment(ref frames) != 4) return;
            var bytes = new byte[size];
            Marshal.Copy(buffer, bytes, 0, size);
            Volatile.Read(ref tcs).TrySetResult(bytes);
        };

        try
        {
            media.AddOption(":start-time=" + (durationMs * 0.10 / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
            media.AddOption(":no-audio");
            media.AddOption(":network-caching=1500");

            using var player = new MediaPlayer(media);
            player.SetVideoFormat("RV32", (uint)w, (uint)h, (uint)pitch);
            player.SetVideoCallbacks(lockCb, null, displayCb);
            player.EncounteredError += (_, _) => Volatile.Read(ref tcs).TrySetCanceled();
            player.EndReached += (_, _) => Volatile.Read(ref tcs).TrySetCanceled();
            player.Play();

            byte[]? result = null;
            await Task.WhenAny(tcs.Task, Task.Delay(25_000, token));
            if (tcs.Task.IsCompletedSuccessfully) result = tcs.Task.Result;

            if (result is not null && AverageBrightness(result) < 18 && !token.IsCancellationRequested)
            {
                Volatile.Write(ref tcs, new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously));
                Interlocked.Exchange(ref frames, 0);
                player.Time = (long)(durationMs * 0.30);
                await Task.WhenAny(tcs.Task, Task.Delay(20_000, token));
                if (tcs.Task.IsCompletedSuccessfully) result = tcs.Task.Result;
            }

            await Task.Run(player.Stop); // never Stop() from a libvlc callback thread
            token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            GC.KeepAlive(lockCb);
            GC.KeepAlive(displayCb);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private static double AverageBrightness(byte[] bgrx)
    {
        long sum = 0;
        int count = 0;
        for (var i = 0; i + 2 < bgrx.Length; i += 4 * 37)
        {
            sum += bgrx[i] + bgrx[i + 1] + bgrx[i + 2];
            count += 3;
        }
        return count == 0 ? 0 : (double)sum / count;
    }

    private static void SaveJpeg(byte[] bgrx, int w, int h, int stride, string path)
    {
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, bgrx, stride);
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) encoder.Save(fs);
        File.Move(tmp, path, overwrite: true);
    }

    public static ImageSource? LoadImage(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(File.ReadAllBytes(path)); // don't keep the file locked
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
        // The LibVLC instance is left for the GC/process exit: disposing it
        // while the worker might still be mid-frame-grab risks a native crash.
    }
}
