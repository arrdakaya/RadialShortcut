namespace RadialShortcut.Core;

public enum MediaKind { Other, Image, Video, Audio, Pdf }

/// <summary>What a file is, judged by its extension, and what the file tools can turn it into.</summary>
public static class MediaKinds
{
    private static readonly HashSet<string> Images = Set("png jpg jpeg webp bmp gif tif tiff heic heif avif ico jfif");
    private static readonly HashSet<string> Videos = Set("mp4 mov mkv webm avi wmv m4v flv 3gp ts mts");
    private static readonly HashSet<string> Audios = Set("mp3 m4a wav ogg opus flac aac wma");

    /// <summary>Formats WPF reads and writes on its own; everything else goes through ffmpeg.</summary>
    public static readonly HashSet<string> NativeImages = Set("png jpg jpeg bmp gif tif tiff jfif");

    public static MediaKind Of(string path)
    {
        var ext = Extension(path);
        return Images.Contains(ext) ? MediaKind.Image
            : Videos.Contains(ext) ? MediaKind.Video
            : Audios.Contains(ext) ? MediaKind.Audio
            : ext == "pdf" ? MediaKind.Pdf
            : MediaKind.Other;
    }

    /// <summary>Target extensions for "Dönüştür", the file's own format left out.</summary>
    public static IReadOnlyList<string> ConvertTargets(string path)
    {
        string[] all = Of(path) switch
        {
            MediaKind.Image => ["png", "jpg", "webp", "pdf", "ico"],
            MediaKind.Video => ["mp4", "gif", "webm", "mp3", "m4a"],
            MediaKind.Audio => ["mp3", "m4a", "wav", "ogg"],
            MediaKind.Pdf => ["png", "jpg"],
            _ => [],
        };
        var own = Extension(path) switch { "jpeg" or "jfif" => "jpg", var e => e };
        return all.Where(t => t != own).ToList();
    }

    /// <summary>"photo.JPG" → "jpg".</summary>
    public static string Extension(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    /// <summary>"a.png" next to itself as "a.webp"; "a (2).webp" when that is taken.</summary>
    public static string FreeName(string directory, string baseName, string extension)
    {
        var file = Path.Combine(directory, $"{baseName}.{extension}");
        for (var i = 2; File.Exists(file) || Directory.Exists(file); i++)
            file = Path.Combine(directory, $"{baseName} ({i}).{extension}");
        return file;
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.00} GB",
    };

    private static HashSet<string> Set(string list) => new(list.Split(' '), StringComparer.OrdinalIgnoreCase);
}
