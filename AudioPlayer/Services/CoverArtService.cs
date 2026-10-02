using System.Windows.Media.Imaging;

namespace AudioPlayer.Services;

public sealed record AudioMetadata(BitmapSource? Cover, string? Artist, string? Album);

public sealed class CoverArtService
{
    private const int MaxPictureBytes = 20 * 1024 * 1024;
    public Task<AudioMetadata> LoadAsync(string audioPath) => Task.Run(() => Load(audioPath));
    public AudioMetadata Load(string audioPath)
    {
        string? artist = null, album = null;
        try
        {
            using var file = TagLib.File.Create(audioPath, TagLib.ReadStyle.None);
            artist = string.Join(" / ", file.Tag.Performers);
            album = file.Tag.Album;
            foreach (var picture in file.Tag.Pictures.OrderBy(p => p.Type == TagLib.PictureType.FrontCover ? 0 : 1))
            {
                if (picture.Data.Count > MaxPictureBytes) continue;
                var image = Decode(picture.Data.Data);
                if (image is not null) return new AudioMetadata(image, artist, album);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException or ArgumentException) { }
        string directory = Path.GetDirectoryName(audioPath) ?? "";
        string name = Path.GetFileNameWithoutExtension(audioPath);
        foreach (string candidate in new[] { name + ".jpg", name + ".png", "cover.jpg", "cover.png", "folder.jpg", "folder.png", "front.jpg" })
        {
            string path = Path.Combine(directory, candidate);
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > MaxPictureBytes) continue;
                var image = Decode(File.ReadAllBytes(path));
                if (image is not null) return new AudioMetadata(image, artist, album);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new AudioMetadata(null, artist, album);
    }
    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 600;
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return null; }
    }
}
