using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace libmsstyle
{
    public class ImageConverter
    {
        public static void PremultiplyAlpha(Stream instream, out Bitmap dst)
        {
            var src = new Bitmap(instream);
            // Premultiply the alpha channel.
            var data = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            // Here, we lie. The data is actually premultiplied, not straight. If we don't lie,
            // the bitmap class will convert the data from PArgb back to Argb when saving as PNG.
            // It does so, because PNG has per specification always straight alpha.
            dst = new Bitmap(src.Width, src.Height, data.Stride, PixelFormat.Format32bppArgb, data.Scan0);
        }

        public static void PremulToStraightAlpha(Stream instream, out Bitmap dst) 
        {
            var src = new Bitmap(instream);
            // Bitmap `src` will have format Argb, because PNGs are always supposed to be Argb and there is no
            // field in a PNG image describing this. LockBits(Argb) will thus perform no conversion.
            var data = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            // This bitmap, we tell the truth about `data` having premultiplied alpha. When saving the
            // bitmap as PNG (done by the caller of this function), the conversion to straight alpha will happen.
            dst = new Bitmap(src.Width, src.Height, data.Stride, PixelFormat.Format32bppPArgb, data.Scan0);
        }

        /// <summary>
        /// True when a 32bpp DIB carries a two-level alpha mask: some pixels fully transparent, some fully
        /// opaque. xp itself only blends alpha when a pixel is partially transparent (utils.cpp
        /// PreMultiplyAlpha), so a 0/255 mask only means anything where xp copies the bitmap into a 32bpp
        /// DIB and keeps its alpha (see VisualStyle.XpKeepsImageAlpha).
        /// </summary>
        public static bool HasAlphaMask(byte[] dib)
        {
            if (dib == null || dib.Length < 40) return false;
            short biBitCount = BitConverter.ToInt16(dib, 14);
            if (biBitCount != 32) return false;
            int biSize = BitConverter.ToInt32(dib, 0);
            int width = BitConverter.ToInt32(dib, 4);
            int height = Math.Abs(BitConverter.ToInt32(dib, 8));
            int biCompression = BitConverter.ToInt32(dib, 16);
            int biClrUsed = BitConverter.ToInt32(dib, 32);
            int pixelOffset = biSize + (biCompression == 3 && biSize == 40 ? 12 : 0) + Math.Max(0, biClrUsed) * 4;
            int end = Math.Min(dib.Length, pixelOffset + width * 4 * height);
            bool transparent = false, opaque = false;
            for (int p = pixelOffset + 3; p < end; p += 4)
            {
                if (dib[p] == 0) transparent = true;
                else if (dib[p] == 255) opaque = true;
                if (transparent && opaque) return true;
            }
            return false;
        }

        /// <summary>
        /// Converts an XP RT_BITMAP (raw DIB) into a png the way uxtheme stores IMAGE resources:
        /// premultiplied ARGB.
        /// 32bpp: XP's themeldr only uses the alpha channel when some pixel is partially
        /// transparent (utils.cpp PreMultiplyAlpha "true alpha"); otherwise the image is opaque.
        /// <paramref name="transparentKey"/>: pixels of exactly that rgb become alpha 0 (XP's
        /// TRANSPARENT / TRANSPARENTCOLOR color key, applied to non-alpha images only).
        /// <paramref name="anyAlpha"/>: DirectUI's rule instead (shellstyle rcbmp mode 2, directui
        /// convert.cpp HasAlphaChannel): any non-zero alpha means the alpha channel is used, so
        /// 0/255-only bitmaps keep their transparent pixels.
        /// </summary>
        public static byte[] ConvertXpDibToPremultipliedPng(byte[] dib, Color? transparentKey, bool anyAlpha = false)
        {
            short biBitCount = BitConverter.ToInt16(dib, 14);
            using (Bitmap bmp = biBitCount == 32 ? CreateArgbBitmapFromDib(dib) : CreateBitmapFromDib(dib))
            {
                int w = bmp.Width, h = bmp.Height;
                var px = new int[w * h];
                var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < h; ++y) Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), px, y * w, w);
                }
                finally { bmp.UnlockBits(bd); }

                bool trueAlpha = false;
                if (biBitCount == 32)
                {
                    foreach (int p in px)
                    {
                        uint a = (uint)p >> 24;
                        if (anyAlpha ? a != 0 : a != 0 && a != 255) { trueAlpha = true; break; }
                    }
                }

                int key = transparentKey.HasValue ? transparentKey.Value.ToArgb() & 0xFFFFFF : -1;
                for (int i = 0; i < px.Length; ++i)
                {
                    uint p = (uint)px[i];
                    if (trueAlpha)
                    {
                        uint a = p >> 24;
                        uint r = ((p >> 16) & 0xFF) * a / 255, g = ((p >> 8) & 0xFF) * a / 255, b = (p & 0xFF) * a / 255;
                        px[i] = (int)((a << 24) | (r << 16) | (g << 8) | b);
                    }
                    else if (key >= 0 && (int)(p & 0xFFFFFF) == key)
                    {
                        px[i] = 0;
                    }
                    else
                    {
                        px[i] = (int)(p | 0xFF000000);
                    }
                }

                // stored premultiplied: write the pixels as plain ARGB so GDI+ doesn't un-premultiply.
                using (var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    var od = outBmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        for (int y = 0; y < h; ++y) Marshal.Copy(px, y * w, IntPtr.Add(od.Scan0, y * od.Stride), w);
                    }
                    finally { outBmp.UnlockBits(od); }
                    using (var ms = new MemoryStream())
                    {
                        outBmp.Save(ms, ImageFormat.Png);
                        return ms.ToArray();
                    }
                }
            }
        }

        /// <summary>
        /// Manually constructs a 32bpp ARGB Bitmap from raw DIB data.
        /// GDI+ loads 32bpp BMPs as Format32bppRgb, discarding alpha — this bypasses that.
        /// </summary>
        private static Bitmap CreateArgbBitmapFromDib(byte[] dib)
        {
            int biSize = BitConverter.ToInt32(dib, 0);
            int biWidth = BitConverter.ToInt32(dib, 4);
            int biHeight = BitConverter.ToInt32(dib, 8);
            int biCompression = BitConverter.ToInt32(dib, 16);

            bool bottomUp = biHeight > 0;
            int height = Math.Abs(biHeight);

            // For BI_BITFIELDS (3) with standard 40-byte header, 3 DWORD masks follow
            int extraBytes = 0;
            if (biCompression == 3 && biSize == 40)
                extraBytes = 12;

            int pixelOffset = biSize + extraBytes;
            int stride = biWidth * 4;

            var bmp = new Bitmap(biWidth, height, PixelFormat.Format32bppArgb);
            var bmpData = bmp.LockBits(new Rectangle(0, 0, biWidth, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            for (int y = 0; y < height; y++)
            {
                int srcY = bottomUp ? (height - 1 - y) : y;
                int srcOff = pixelOffset + srcY * stride;
                IntPtr dstPtr = IntPtr.Add(bmpData.Scan0, y * bmpData.Stride);
                if (srcOff + stride <= dib.Length)
                    Marshal.Copy(dib, srcOff, dstPtr, stride);
            }

            bmp.UnlockBits(bmpData);
            return bmp;
        }

        /// <summary>
        /// Creates a Bitmap from non-32bpp DIB data using GDI+ with a prepended file header.
        /// Returns a deep copy that is independent of any stream.
        /// </summary>
        private static Bitmap CreateBitmapFromDib(byte[] dib)
        {
            byte[] bmpFile = PrependBitmapFileHeader(dib);
            using (var ms = new MemoryStream(bmpFile))
            using (var temp = new Bitmap(ms))
            {
                return new Bitmap(temp);
            }
        }

        private static byte[] PrependBitmapFileHeader(byte[] dib)
        {
            int biSize = BitConverter.ToInt32(dib, 0);
            short biBitCount = BitConverter.ToInt16(dib, 14);
            int biCompression = BitConverter.ToInt32(dib, 16);
            int biClrUsed = BitConverter.ToInt32(dib, 32);

            int colorTableSize = 0;
            if (biBitCount <= 8)
            {
                int colors = biClrUsed > 0 ? biClrUsed : (1 << biBitCount);
                colorTableSize = colors * 4;
            }
            else
            {
                // BI_BITFIELDS masks, then an optional palette of biClrUsed entries: 16/24/32 bpp DIBs may still
                // carry one (appows' 24bpp separators have 16), and the pixels start after it.
                if (biCompression == 3 && biSize == 40) colorTableSize = 12;
                colorTableSize += Math.Max(0, biClrUsed) * 4;
            }

            int fileHeaderSize = 14;
            int pixelDataOffset = fileHeaderSize + biSize + colorTableSize;

            byte[] bmpFile = new byte[fileHeaderSize + dib.Length];
            bmpFile[0] = 0x42; // 'B'
            bmpFile[1] = 0x4D; // 'M'
            Array.Copy(BitConverter.GetBytes(bmpFile.Length), 0, bmpFile, 2, 4);
            Array.Copy(BitConverter.GetBytes(pixelDataOffset), 0, bmpFile, 10, 4);
            Array.Copy(dib, 0, bmpFile, fileHeaderSize, dib.Length);

            return bmpFile;
        }
    }
}
