using System.Drawing;

namespace Legacy.Core
{
    public sealed class Thumbnails
    {
        public Size Fit(Size original, int max)
        {
            var scale = (double)max / System.Math.Max(original.Width, original.Height);
            return new Size((int)(original.Width * scale), (int)(original.Height * scale));
        }

        public Bitmap Blank(int width, int height)
        {
            return new Bitmap(width, height);
        }
    }
}
