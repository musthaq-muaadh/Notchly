using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Notchly
{
    public sealed class ShelfItem
    {
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public ImageSource? Icon { get; init; }

        public static ShelfItem FromPath(string path, bool stripExtension)
        {
            var trimmed = path.TrimEnd('\\', '/');
            var name = stripExtension
                ? System.IO.Path.GetFileNameWithoutExtension(trimmed)
                : System.IO.Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name)) name = trimmed;
            return new ShelfItem { Path = path, Name = name, Icon = GetIcon(path) };
        }

        static ImageSource? GetIcon(string path)
        {
            try
            {
                using var ic = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (ic == null) return null;
                var src = Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
        }
    }
}
