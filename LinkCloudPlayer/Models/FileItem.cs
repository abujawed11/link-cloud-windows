using System.Text.Json.Serialization;

namespace LinkCloudPlayer.Models;

public class FileItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("original_name")]
    public string OriginalName { get; set; } = "";

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }

    [JsonPropertyName("source_url")]
    public string SourceUrl { get; set; } = "";

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    public bool IsVideo => (MimeType ?? "").StartsWith("video/");
    public bool IsAudio => (MimeType ?? "").StartsWith("audio/");

    public string SizeLabel
    {
        get
        {
            double size = SizeBytes;
            string[] units = ["B", "KB", "MB", "GB"];
            int i = 0;
            while (size >= 1024 && i < units.Length - 1)
            {
                size /= 1024;
                i++;
            }
            return $"{size:0.#} {units[i]}";
        }
    }
}
