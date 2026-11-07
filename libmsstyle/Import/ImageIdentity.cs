using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace libmsstyle.Import
{
    public class ImageInfo
    {
        public string Format { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool HasAlpha { get; set; }
        /// <summary>sha1 over straight-alpha ARGB pixels plus size. Same picture, different png encoder => same hash.</summary>
        public string PixelSha1 { get; set; }
        public string ByteSha1 { get; set; }
        public string Error { get; set; }
        public bool Decodable => Error == null;
    }

    /// <summary>A decoded 32bpp straight-alpha ARGB picture, one uint per pixel, row-major.</summary>
    public class ArgbImage
    {
        public int Width { get; }
        public int Height { get; }
        public uint[] Pixels { get; }

        public ArgbImage(int w, int h)
        {
            Width = w;
            Height = h;
            Pixels = new uint[w * h];
        }

        public ArgbImage(int w, int h, uint[] pixels)
        {
            if (pixels.Length != w * h) throw new ArgumentException("pixel buffer doesn't match the image size");
            Width = w;
            Height = h;
            Pixels = pixels;
        }

        public ArgbImage Crop(Rectangle r)
        {
            if (r.X < 0 || r.Y < 0 || r.Right > Width || r.Bottom > Height || r.Width < 0 || r.Height < 0)
                throw new ArgumentOutOfRangeException(nameof(r), $"crop {r} is outside {Width}x{Height}");
            var dst = new ArgbImage(r.Width, r.Height);
            for (int y = 0; y < r.Height; ++y)
                Array.Copy(Pixels, (r.Y + y) * Width + r.X, dst.Pixels, y * r.Width, r.Width);
            return dst;
        }

        public void Blit(ArgbImage src, int x, int y)
        {
            if (x < 0 || y < 0 || x + src.Width > Width || y + src.Height > Height)
                throw new ArgumentOutOfRangeException(nameof(src), "blit target is outside the image");
            for (int row = 0; row < src.Height; ++row)
                Array.Copy(src.Pixels, row * src.Width, Pixels, (y + row) * Width + x, src.Width);
        }

        /// <summary>
        /// Straight-alpha source over the destination. Blit copies raw pixels, which punches a hole in
        /// the destination wherever the source is transparent (a glyph drawn onto its button face).
        /// </summary>
        public void BlendOver(ArgbImage src, int x, int y)
        {
            if (x < 0 || y < 0 || x + src.Width > Width || y + src.Height > Height)
                throw new ArgumentOutOfRangeException(nameof(src), "blend target is outside the image");
            for (int row = 0; row < src.Height; ++row)
            {
                for (int col = 0; col < src.Width; ++col)
                {
                    uint s = src.Pixels[row * src.Width + col];
                    int sa = (int)(s >> 24);
                    if (sa == 0) continue;
                    int di = (y + row) * Width + x + col;
                    uint d = Pixels[di];
                    if (sa == 255) { Pixels[di] = s; continue; }
                    int da = (int)(d >> 24);
                    int r = ((int)((s >> 16) & 0xFF) * sa + (int)((d >> 16) & 0xFF) * da * (255 - sa) / 255) / 255;
                    int g = ((int)((s >> 8) & 0xFF) * sa + (int)((d >> 8) & 0xFF) * da * (255 - sa) / 255) / 255;
                    int b = ((int)(s & 0xFF) * sa + (int)(d & 0xFF) * da * (255 - sa) / 255) / 255;
                    int a = sa + da * (255 - sa) / 255;
                    Pixels[di] = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
                }
            }
        }

        public bool RegionEquals(Rectangle r, ArgbImage other, Rectangle o)
        {
            if (r.Width != o.Width || r.Height != o.Height) return false;
            for (int y = 0; y < r.Height; ++y)
                for (int x = 0; x < r.Width; ++x)
                    if (Pixels[(r.Y + y) * Width + r.X + x] != other.Pixels[(o.Y + y) * other.Width + o.X + x])
                        return false;
            return true;
        }

        public string PixelSha1()
        {
            var bytes = new byte[8 + Pixels.Length * 4];
            BitConverter.GetBytes(Width).CopyTo(bytes, 0);
            BitConverter.GetBytes(Height).CopyTo(bytes, 4);
            Buffer.BlockCopy(Pixels, 0, bytes, 8, Pixels.Length * 4);
            return ResourceInventory.Sha1Hex(bytes);
        }
    }

    public static class ImageIdentity
    {
        public static string SniffFormat(byte[] data)
        {
            if (data == null || data.Length < 8) return "unknown";
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return "png";
            if (data[0] == 0x42 && data[1] == 0x4D) return "bmp";
            // headerless DIB (BITMAPINFOHEADER size 40/108/124)
            int hdr = BitConverter.ToInt32(data, 0);
            if (hdr == 40 || hdr == 108 || hdr == 124) return "dib";
            return "unknown";
        }

        public static ImageInfo Analyze(byte[] data)
        {
            var info = new ImageInfo
            {
                Format = SniffFormat(data),
                ByteSha1 = data == null ? null : ResourceInventory.Sha1Hex(data)
            };
            if (info.Format != "png")
            {
                // msstyles from vista on store PNG; anything else is reported, not guessed at.
                info.Error = "not a png (" + info.Format + ")";
                return info;
            }
            try
            {
                var img = Decode(data);
                info.Width = img.Width;
                info.Height = img.Height;
                info.PixelSha1 = img.PixelSha1();
                foreach (var p in img.Pixels)
                {
                    if ((p >> 24) != 0xFF) { info.HasAlpha = true; break; }
                }
            }
            catch (Exception ex)
            {
                info.Error = "png decode failed: " + ex.Message;
            }
            return info;
        }

        public static ArgbImage Decode(byte[] png)
        {
            using (var ms = new MemoryStream(png))
            using (var bmp = new Bitmap(ms))
            {
                var img = new ArgbImage(bmp.Width, bmp.Height);
                var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new int[bmp.Width];
                    for (int y = 0; y < bmp.Height; ++y)
                    {
                        Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, bmp.Width);
                        Buffer.BlockCopy(row, 0, img.Pixels, y * bmp.Width * 4, bmp.Width * 4);
                    }
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }
                return img;
            }
        }

        /// <summary>
        /// Encodes straight-alpha ARGB to PNG through GDI+, then decodes the result again and
        /// throws if a single pixel changed. Never hand back an atlas that doesn't round-trip.
        /// </summary>
        public static byte[] EncodePngVerified(ArgbImage img)
        {
            byte[] png;
            using (var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb))
            {
                var bd = bmp.LockBits(new Rectangle(0, 0, img.Width, img.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new int[img.Width];
                    for (int y = 0; y < img.Height; ++y)
                    {
                        Buffer.BlockCopy(img.Pixels, y * img.Width * 4, row, 0, img.Width * 4);
                        Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, img.Width);
                    }
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    png = ms.ToArray();
                }
            }

            var check = Decode(png);
            if (check.Width != img.Width || check.Height != img.Height)
                throw new InvalidDataException("png re-encode changed the image size");
            for (int i = 0; i < img.Pixels.Length; ++i)
            {
                if (check.Pixels[i] != img.Pixels[i])
                    throw new InvalidDataException($"png re-encode changed pixel {i % img.Width},{i / img.Width}");
            }
            return png;
        }
    }
}
