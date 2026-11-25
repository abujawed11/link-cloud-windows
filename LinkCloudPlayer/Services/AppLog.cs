using System.IO;
using System.Text.RegularExpressions;

namespace LinkCloudPlayer.Services;

/// <summary>
/// The one place the app writes log files (crash.log, vlc.log under
/// %AppData%\LinkCloudPlayer). Every line is:
///  - redacted: stream URLs carry the login JWT as <c>?token=…</c>, and
///    anyone holding that string can use the account until it expires, so it
///    must never reach disk;
///  - size-capped: when a file passes <see cref="MaxBytes"/> it is moved to
///    "&lt;name&gt;.old.log" (replacing any previous one) and a new file is
///    started, so logs can't grow without limit.
/// Logging failures are swallowed: logging must never crash the app or
/// interrupt playback.
/// </summary>
public static partial class AppLog
{
    public const long MaxBytes = 5 * 1024 * 1024;

    public static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LinkCloudPlayer");

    private static readonly object Gate = new();

    // token=<anything up to a URL/word delimiter>, also URL-encoded "token%3D".
    [GeneratedRegex(@"(token(?:=|%3D))[^&\s'""<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRx();

    // A bare JWT (header.payload.signature) that slipped through without "token=".
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex JwtRx();

    public static string Redact(string text)
    {
        text = TokenRx().Replace(text, "$1[redacted]");
        return JwtRx().Replace(text, "[redacted-jwt]");
    }

    /// <summary>Appends one redacted, timestamped entry to the named log file.</summary>
    public static void Write(string fileName, string message)
    {
        try
        {
            var line = $"[{DateTime.Now:O}] {Redact(message)}{Environment.NewLine}";
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = Path.Combine(Directory, fileName);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(path, Path.ChangeExtension(path, ".old.log"), overwrite: true);
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // never let logging break the app
        }
    }

    /// <summary>
    /// One-time cleanup of logs written by older builds, which logged VLC at
    /// debug level (tens of MB a day) with the login token in plain text.
    /// Rewrites each existing log with tokens redacted, keeping only its tail
    /// if it's over the size cap.
    /// </summary>
    public static void ScrubExistingLogs()
    {
        foreach (var name in new[] { "vlc.log", "vlc.old.log", "crash.log", "crash.old.log", "thumbs.log" })
        {
            try
            {
                lock (Gate) ScrubOne(name);
            }
            catch
            {
                // best effort
            }
        }
    }

    private static void ScrubOne(string name)
    {
        var path = Path.Combine(Directory, name);
        if (!File.Exists(path)) return;

        if (name == "thumbs.log") { File.Delete(path); return; } // leftover debug trace

        var info = new FileInfo(path);
        if (info.Length <= MaxBytes)
        {
            var text = File.ReadAllText(path);
            var clean = Redact(text);
            if (clean != text) File.WriteAllText(path, clean);
            return;
        }

        // Oversized: keep the last MaxBytes (redacted), drop the rest.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(-MaxBytes, SeekOrigin.End);
        using var reader = new StreamReader(fs);
        reader.ReadLine(); // skip the partial first line
        var tail = Redact(reader.ReadToEnd());
        reader.Dispose();
        File.WriteAllText(path, tail);
    }
}
