using libmsstyle;
using libmsstyle.Import;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;

namespace libmsstyleTests
{
    [TestClass]
    public class XpImportTests
    {
        const string XpLuna = @"..\..\..\styles\xp_luna\luna.msstyles";
        // the win10 luna port isn't in the repo; point MSSTYLE_LUNA10 at ported_to_10_already\Luna\Luna.msstyles.
        static string Luna10 => Environment.GetEnvironmentVariable("MSSTYLE_LUNA10");

        static StyleProperty Find(VisualStyle s, string cls, int part, int state, IDENTIFIER id)
        {
            var c = s.Classes.Values.Single(x => x.ClassName.Equals(cls, StringComparison.OrdinalIgnoreCase));
            return c.Parts.TryGetValue(part, out var p) && p.States.TryGetValue(state, out var st)
                ? st.Properties.FirstOrDefault(x => x.Header.nameID == (int)id) : null;
        }

        [TestMethod]
        public void XpSectionsGetSchemaIds()
        {
            using (var s = new VisualStyle())
            {
                s.Load(XpLuna);
                Assert.AreEqual(Platform.WinXP, s.Platform);
                // [Window.Caption] SizingMargins = 28, 35, 9, 17 is MARGINS left, right, top, bottom.
                var m = (Margins)Find(s, "Window", 1, 0, IDENTIFIER.SIZINGMARGINS).GetValue();
                Assert.AreEqual(new Margins(28, 9, 35, 17), m);
                // [button.pushbutton(disabled)] -> PUSHBUTTON = 1, PBS_DISABLED = 4.
                Assert.IsNotNull(Find(s, "Button", 1, 4, IDENTIFIER.TEXTCOLOR));
                // [Window.CloseButton] -> part 18.
                Assert.AreEqual(8, Find(s, "Window", 18, 0, IDENTIFIER.IMAGECOUNT).GetValue());
                // "Font = Tahoma, 8" in [Rebar] is TMT_FONT, held as a string id.
                var font = Find(s, "Rebar", 0, 0, IDENTIFIER.FONT);
                Assert.AreEqual("Tahoma, 8", s.PreferredStringTable[font.Header.shortFlag]);
                Assert.AreEqual(0, s.XpDiagnostics.Count, string.Join("\n", s.XpDiagnostics));
            }
        }

        [TestMethod]
        public void SchemesComeFromThePackedTablesNotTheFileSections()
        {
            // Lion OS X: [ColorScheme.NormalColor], but every [File.X] says "ColorSchemes = Lion". packthem still
            // built COMBODATA (1 color, 3 sizes -> files 0, 1, 2) and XP loads that.
            var ini = System.Text.Encoding.Unicode.GetBytes(
                "[ColorScheme.NormalColor]\r\nDisplayName = Lion3\r\n[Size.NormalSize]\r\nDisplayName = Normal\r\n" +
                "[Size.LargeFonts]\r\nDisplayName = Large Fonts\r\n[File.NormalLion]\r\nColorSchemes = Lion\r\nSizes = NormalSize\r\n");
            Assert.AreEqual(0, XpStyleIniParser.ParseThemesIni(ini).Count);

            var combo = new byte[] { 1, 0, 3, 0, 0, 0, 1, 0, 2, 0 };
            var schemes = XpStyleIniParser.SchemesFromCombo(combo, new System.Collections.Generic.List<string> { "NormalColor" },
                new System.Collections.Generic.List<string> { "NormalSize", "LargeFonts", "ExtraLargeFonts" },
                new System.Collections.Generic.List<string> { "NORMALLION_INI", "LARGEFONTSLION_INI", "EXTRALARGELION_INI" }, ini);
            Assert.AreEqual(1, schemes.Count);
            Assert.AreEqual("NormalColor", schemes[0].InternalName);
            Assert.AreEqual("Lion3", schemes[0].DisplayName);
            Assert.AreEqual("NORMALLION_INI", schemes[0].IniResourceName);
            CollectionAssert.AreEqual(new[] { "LargeFonts", "ExtraLargeFonts" }, schemes[0].Sizes.Select(x => x.InternalName).ToArray());
            Assert.AreEqual("Large Fonts", schemes[0].Sizes[0].DisplayName);
            Assert.AreEqual("EXTRALARGELION_INI", schemes[0].Sizes[1].IniResourceName);

            // a size the color has no file for (-1) isn't offered.
            var sparse = new byte[] { 1, 0, 2, 0, 0, 0, 0xFF, 0xFF };
            Assert.AreEqual(0, XpStyleIniParser.SchemesFromCombo(sparse, new System.Collections.Generic.List<string> { "NormalColor" },
                new System.Collections.Generic.List<string> { "NormalSize", "LargeFonts" },
                new System.Collections.Generic.List<string> { "NORMALLION_INI" }, ini)[0].Sizes.Count);
        }

        [TestMethod]
        public void MissingImageLayoutMeansHorizontalLikeXp()
        {
            using (var s = new VisualStyle())
            {
                s.Load(XpLuna);
                // [ScrollBar.SizeBox] has no ImageLayout; xp slices its 2 frames side by side.
                var sizebox = s.Classes.Values.Single(c => c.ClassName.Equals("ScrollBar", StringComparison.OrdinalIgnoreCase));
                Assert.AreEqual(1, SemanticModel.FindEffective(s, sizebox, 10, 0, IDENTIFIER.IMAGELAYOUT).GetValue());
            }
        }

        [TestMethod]
        public void XpBitmapsConvertLikeTheWin10Port()
        {
            using (var s = new VisualStyle())
            {
                s.Load(XpLuna);
                // [Window.Caption] is an 8bpp FrameCaption.bmp, Transparent = true, no color: magenta is the key.
                var caption = ImageIdentity.Decode(s.GetRawResource("IMAGE", Find(s, "Window", 1, 0, IDENTIFIER.IMAGEFILE).Header.shortFlag));
                Assert.IsTrue(caption.Pixels.Any(x => x == 0), "no keyed pixel");
                Assert.IsTrue(caption.Pixels.All(x => x == 0 || (x >> 24) == 0xFF), "8bpp art got partial alpha");
                // the Luna port's IMAGE 1007 is the same picture; pixel identity, not a near match.
                Assert.AreEqual("378d5fc9d90a0ad3f0f6af54d5f93a279f3ef2a3", caption.PixelSha1());

                // closebutton.bmp is 32bpp with real alpha: stored premultiplied, like win10 IMAGE resources.
                var close = ImageIdentity.Decode(s.GetRawResource("IMAGE", Find(s, "Window", 18, 0, IDENTIFIER.IMAGEFILE).Header.shortFlag));
                Assert.IsTrue(close.Pixels.Any(x => (x >> 24) is uint al && al > 0 && al < 255), "alpha lost");
                Assert.IsTrue(close.Pixels.All(x => ((x >> 16) & 0xFF) <= x >> 24 && ((x >> 8) & 0xFF) <= x >> 24 && (x & 0xFF) <= x >> 24), "not premultiplied");
            }
        }

        [TestMethod]
        public void TrueColorDibWithAPaletteStartsAfterIt()
        {
            // appows' 24bpp separators carry biClrUsed = 16 palette entries GDI skips; reading pixels from
            // right after the header turned the strip into palette garbage and lost its red color key.
            const int w = 2, h = 1, clrUsed = 2;
            var dib = new byte[40 + clrUsed * 4 + 8];
            BitConverter.GetBytes(40).CopyTo(dib, 0);
            BitConverter.GetBytes(w).CopyTo(dib, 4);
            BitConverter.GetBytes(h).CopyTo(dib, 8);
            BitConverter.GetBytes((short)1).CopyTo(dib, 12);
            BitConverter.GetBytes((short)24).CopyTo(dib, 14);
            BitConverter.GetBytes(clrUsed).CopyTo(dib, 32);
            for (int i = 40; i < 40 + clrUsed * 4; ++i) dib[i] = 0x77; // palette, not pixels
            int px = 40 + clrUsed * 4;
            dib[px + 0] = 0; dib[px + 1] = 0; dib[px + 2] = 255;     // red (BGR): the key
            dib[px + 3] = 10; dib[px + 4] = 20; dib[px + 5] = 30;    // rgb(30,20,10)

            var img = ImageIdentity.Decode(ImageConverter.ConvertXpDibToPremultipliedPng(dib, System.Drawing.Color.FromArgb(255, 0, 0)));
            Assert.AreEqual(0u, img.Pixels[0], "red key not transparent");
            Assert.AreEqual(0xFF1E140Au, img.Pixels[1]);
        }

        [TestMethod]
        public void ImageGlyphSizeSlotsUseTheGlyphColorKey()
        {
            using (var s = new VisualStyle())
            {
                s.Load(XpLuna);
                // stock luna's [ScrollBar.ArrowBtn]: GlyphType = ImageGlyph, ImageSelectType = Size,
                // GlyphTransparent = true and no Transparent. ImageFile1/2 are glyph sizes.
                var sb = s.Classes.Values.Single(c => c.ClassName.Equals("ScrollBar", StringComparison.OrdinalIgnoreCase));
                Assert.AreEqual(System.Drawing.Color.FromArgb(255, 0, 255), s.XpColorKey(sb, 1, 0, (int)IDENTIFIER.IMAGEFILE1));
                Assert.AreEqual(System.Drawing.Color.FromArgb(255, 0, 255), s.XpColorKey(sb, 1, 0, (int)IDENTIFIER.IMAGEFILE2));
                // the background strip still follows TRANSPARENT, which this part doesn't set.
                Assert.IsNull(s.XpColorKey(sb, 1, 0, (int)IDENTIFIER.IMAGEFILE));
            }
        }

        [TestMethod]
        public void StartPanelLogoffButtonsKeepTheirBitmapAlphaMask()
        {
            // xp draws StartPanel LOGOFFBUTTONS with DTBG_DRAWSOLID (nt5src logoff.cpp) and uxtheme then
            // copies the bitmap's alpha into the log off toolbar's ILC_COLOR32 imagelist, so a 0/255 mask
            // is what xp showed even when the record says Transparent = false (Xanadu's log off and shut
            // down icons on magenta). Other parts only get the color key.
            using (var s = new VisualStyle())
            {
                s.Load(XpLuna);
                var start = s.Classes.Values.Single(c => c.ClassName.Equals("StartPanel", StringComparison.OrdinalIgnoreCase));
                Assert.IsTrue(VisualStyle.XpKeepsImageAlpha(start, 9));
                Assert.IsFalse(VisualStyle.XpKeepsImageAlpha(start, 8));
                var button = s.Classes.Values.Single(c => c.ClassName.Equals("Button", StringComparison.OrdinalIgnoreCase));
                Assert.IsFalse(VisualStyle.XpKeepsImageAlpha(button, 1));
            }
        }

        [TestMethod]
        public void AlphaMaskOnlyCountsForPartsXpKeepsTheAlphaOf()
        {
            // 2x1 32bpp DIB: one transparent magenta pixel, one opaque white pixel. xp ignores a 0/255-only
            // alpha channel (utils.cpp PreMultiplyAlpha wants a partial pixel), so it is only honored where
            // xp keeps the bitmap's alpha.
            int w = 2, h = 1;
            var dib = new byte[40 + w * h * 4];
            BitConverter.GetBytes(40).CopyTo(dib, 0);
            BitConverter.GetBytes(w).CopyTo(dib, 4);
            BitConverter.GetBytes(h).CopyTo(dib, 8);
            BitConverter.GetBytes((short)1).CopyTo(dib, 12);
            BitConverter.GetBytes((short)32).CopyTo(dib, 14);
            int px = 40;
            dib[px + 0] = 255; dib[px + 1] = 0; dib[px + 2] = 255; dib[px + 3] = 0;     // magenta, transparent
            dib[px + 4] = 255; dib[px + 5] = 255; dib[px + 6] = 255; dib[px + 7] = 255; // white, opaque

            Assert.IsTrue(ImageConverter.HasAlphaMask(dib));
            var masked = ImageIdentity.Decode(ImageConverter.ConvertXpDibToPremultipliedPng(dib, null, anyAlpha: true));
            Assert.AreEqual(0u, masked.Pixels[0], "masked pixel not transparent");
            Assert.AreEqual(0xFFFFFFFFu, masked.Pixels[1]);

            var opaque = ImageIdentity.Decode(ImageConverter.ConvertXpDibToPremultipliedPng(dib, null));
            Assert.AreEqual(0xFFFF00FFu, opaque.Pixels[0], "xp's default draw keeps the mask pixel opaque");

            dib[px + 3] = 255;
            Assert.IsFalse(ImageConverter.HasAlphaMask(dib), "all-opaque alpha is not a mask");
        }

        [TestMethod]
        public void StockLunaOntoTheLunaPortChangesNothing()
        {
            if (Luna10 == null || !File.Exists(Luna10)) Assert.Inconclusive("set MSSTYLE_LUNA10 to the win10 Luna.msstyles port");
            var r = Win7StyleImporter.Run(new Win7ImportOptions
            {
                SourcePath = XpLuna,
                SourcePlatform = Platform.WinXP,
                SourceScheme = "NormalColor",
                BaseScheme = "NormalColor",
                // the stock shellstyle as the source's: the task pane must stay the template's too.
                SourceShellStylePath = StockStyles.XpLunaShellPath("NormalColor"),
                TemplatePath = Luna10,
                DryRun = true,
            });
            Assert.AreEqual(ImportStatus.ReadyForVmTest, r.Status, string.Join("\n", r.Errors));
            var changes = r.Decisions.Where(d => d.Action.StartsWith("applied") || d.Action.StartsWith("added") || d.Action.StartsWith("copied") || d.Action.StartsWith("removed")).ToList();
            Assert.AreEqual(0, changes.Count, string.Join("\n", changes.Select(d => $"{d.Class}|{d.Part}|{d.State}|{d.Property}: {d.Action}")));
            Assert.IsTrue(r.Validation.Checks.Any(c => c.Contains("VARIANT/NORMAL is byte-identical")));
            // the port re-cut Toolbar BUTTON from 6 to 8 frames with stock frames; that mapping must be learned.
            Assert.IsTrue(r.Notes.Any(n => n.StartsWith("Toolbar part 1 state 0: strip re-cut from 6 vertical to 8 vertical")), string.Join("\n", r.Notes));
        }

        [TestMethod]
        public void XpShellStyleValuesParseLikeTheStylesheet()
        {
            using (var sh = new XpShellStyle(StockStyles.XpLunaShellPath("NormalColor")))
            {
                // [taskpane] element [id=atom(sectionlist)] background: gradient(argb(0,123,162,231), argb(0,99,117,214), 1)
                var fill = XpShellStyle.ParseFill(sh.Get("taskpane", "element [id=atom(sectionlist)]", "background"));
                CollectionAssert.AreEqual(new[] { System.Drawing.Color.FromArgb(123, 162, 231).ToArgb(), System.Drawing.Color.FromArgb(99, 117, 214).ToArgb() }, fill.Select(c => c.ToArgb()).ToArray());
                // selectors match regardless of spacing: "button [id=atom(header)]"
                Assert.IsTrue(XpShellStyle.ParseRcBmp(sh.Get("mainsectionss", "button  [id=atom(header)]", "background"), out int id, out var key));
                Assert.AreEqual(110, id);
                Assert.AreEqual(System.Drawing.Color.FromArgb(255, 0, 255).ToArgb(), key.Value.ToArgb());
                Assert.IsNotNull(sh.GetBitmap(110));
            }
        }

        [TestMethod]
        public void CustomShellStyleRecolorsTheTaskPane()
        {
            if (Luna10 == null || !File.Exists(Luna10)) Assert.Inconclusive("set MSSTYLE_LUNA10 to the win10 Luna.msstyles port");
            // stock homestead's shellstyle on the blue scheme: a "custom" shellstyle whose values all differ.
            var r = Win7StyleImporter.Run(new Win7ImportOptions
            {
                SourcePath = XpLuna,
                SourcePlatform = Platform.WinXP,
                SourceScheme = "NormalColor",
                BaseScheme = "NormalColor",
                SourceShellStylePath = StockStyles.XpLunaShellPath("HomeStead"),
                TemplatePath = Luna10,
                DryRun = true,
            });
            Assert.AreEqual(ImportStatus.ReadyForVmTest, r.Status, string.Join("\n", r.Errors));
            using (var olive = new XpShellStyle(StockStyles.XpLunaShellPath("HomeStead")))
            {
                var want = XpShellStyle.ParseFill(olive.Get("taskpane", "element [id=atom(sectionlist)]", "background"));
                var start = r.Decisions.Single(d => d.Class == "ExplorerBar" && d.Property == "GRADIENTCOLOR1");
                Assert.AreEqual($"rgb({want[0].R},{want[0].G},{want[0].B})", start.Result);
            }
            Assert.IsTrue(r.Decisions.Any(d => d.Class == "ExplorerBar" && d.Part == "SPECIALGROUPHEAD" && d.Property == "IMAGEFILE" && d.Action == "applied-shellstyle"));
        }

        // a custom xp style: point MSSTYLE_XP_TANGOLUX at the extracted TangoLux\TangoLux.msstyles.
        static string TangoLux => Environment.GetEnvironmentVariable("MSSTYLE_XP_TANGOLUX");

        [TestMethod]
        public void MovedStockSlotsAndRetunedMarginsFollowTheSource()
        {
            if (Luna10 == null || !File.Exists(Luna10)) Assert.Inconclusive("set MSSTYLE_LUNA10 to the win10 Luna.msstyles port");
            if (TangoLux == null || !File.Exists(TangoLux)) Assert.Inconclusive("set MSSTYLE_XP_TANGOLUX to TangoLux.msstyles");
            var r = Win7StyleImporter.Run(new Win7ImportOptions
            {
                SourcePath = TangoLux,
                SourcePlatform = Platform.WinXP,
                SourceScheme = "NormalColor",
                BaseScheme = "NormalColor",
                TemplatePath = Luna10,
                DryRun = true,
            });
            Assert.AreEqual(ImportStatus.ReadyForVmTest, r.Status, string.Join("\n", r.Errors));
            // Luna10 moved the stock tray button strip from IMAGEFILE to IMAGEFILE1; the source's strip must land there.
            var tray = r.Decisions.Where(d => d.Class == "TrayNotifyHoriz::Button" && d.Property == "IMAGEFILE1" && d.Result != null).ToList();
            Assert.IsTrue(tray.Count > 0 && tray.All(d => d.Action.StartsWith("applied")), string.Join("\n", tray.Select(d => d.Action)));
            // Luna10 retuned the tray background's SIZINGMARGINS for the stock strip; with TangoLux's strip the source's margins and picture come over.
            var bg = r.Decisions.Where(d => d.Class == "TrayNotifyHoriz::TrayNotify" && d.Part == "BACKGROUND" && (d.Property == "IMAGEFILE" || d.Property == "SIZINGMARGINS")).ToList();
            Assert.AreEqual(2, bg.Count);
            Assert.IsTrue(bg.All(d => d.Result != null), string.Join("\n", bg.Select(d => $"{d.Property}: {d.Action}")));
            // measured against TangoLux on xp: the address box draws its combo image (Luna10's Combobox had no BGTYPE),
            // the task pane padding is the shellstyle's 7rp, and headers lose stock luna's 2px border (top and left:
            // xp places the title at borderthickness + padding, TangoLux 0 + 10 against luna's 2 + 10).
            Assert.IsTrue(r.Decisions.Any(d => d.Class == "Combobox" && d.Part == "Common Properties" && d.State == 0 && d.Property == "BGTYPE" && d.Action == "added"));
            Assert.AreEqual("(l7,t7,r7,b7)", r.Decisions.Single(d => d.Class == "ExplorerBar" && d.Part == "Common Properties" && d.Property == "CONTENTMARGINS" && d.Action == "applied-shellstyle").Result);
            Assert.AreEqual("(l10,t0,r3,b0)", r.Decisions.Single(d => d.Class == "ExplorerBar" && d.Part == "SPECIALGROUPHEAD" && d.Property == "CONTENTMARGINS" && d.Action == "applied-shellstyle").Result);
            // xp tiled the progress chunk art; Luna10 set STRETCH for its own pill. Now that the source's picture and
            // margins are in the part, xp's TILE goes with them or the 18x18 hatch smears across the whole bar.
            var chunk = r.Decisions.Single(d => d.Class == "Progress" && d.Part == "CHUNK" && d.State == 0 && d.Property == "SIZINGTYPE");
            Assert.AreEqual("applied-xp-sizingtype", chunk.Action);
            Assert.AreEqual("TILE", chunk.Result);
        }

        [TestMethod]
        public void MaximizedCaptionKeepsItsOwnArtworkAndXpMetrics()
        {
            string source = Environment.GetEnvironmentVariable("MSSTYLE_XP_ELFIN2");
            if (Luna10 == null || !File.Exists(Luna10)) Assert.Inconclusive("set MSSTYLE_LUNA10 to the win10 Luna.msstyles port");
            if (source == null || !File.Exists(source)) Assert.Inconclusive("set MSSTYLE_XP_ELFIN2 to Elfin2.msstyles");
            string dir = Path.Combine(Path.GetTempPath(), "msstyle-caption-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string output = Path.Combine(dir, "Elfin2.msstyles");
                var report = Win7StyleImporter.Run(new Win7ImportOptions
                {
                    SourcePath = source, SourcePlatform = Platform.WinXP, SourceScheme = "NormalColor",
                    BaseScheme = "NormalColor", TemplatePath = Luna10, OutputPath = output, DryRun = false,
                });
                Assert.AreEqual(ImportStatus.ReadyForVmTest, report.Status, string.Join("\n", report.Errors));
                using (var xp = new VisualStyle())
                using (var port = new VisualStyle())
                {
                    xp.Load(source);
                    port.Load(output);
                    foreach (int part in new[] { 1, 5 })
                    {
                        int xpId = Find(xp, "Window", part, 0, IDENTIFIER.IMAGEFILE).Header.shortFlag & 0xFFFF;
                        int portId = Find(port, "Window", part, 0, IDENTIFIER.IMAGEFILE).Header.shortFlag & 0xFFFF;
                        Assert.AreEqual(ImageIdentity.Decode(xp.GetRawResource("IMAGE", xpId)).PixelSha1(),
                            ImageIdentity.Decode(port.GetRawResource("IMAGE", portId)).PixelSha1(), "caption artwork changed for part " + part);
                        foreach (var id in new[] { IDENTIFIER.SIZINGMARGINS, IDENTIFIER.CONTENTMARGINS, IDENTIFIER.CAPTIONMARGINS, IDENTIFIER.IMAGECOUNT })
                            Assert.AreEqual(Find(xp, "Window", part, 0, id).GetValue(), Find(port, "Window", part, 0, id).GetValue(), "caption geometry changed: " + id);
                    }
                    Assert.AreNotEqual(Find(port, "Window", 1, 0, IDENTIFIER.IMAGEFILE).Header.shortFlag,
                        Find(port, "Window", 5, 0, IDENTIFIER.IMAGEFILE).Header.shortFlag, "maximized caption reused rounded normal artwork");
                    Assert.AreEqual(19, Find(port, "sysmetrics", 0, 0, IDENTIFIER.CAPTIONBARHEIGHT).GetValue());
                    Assert.AreEqual(1, Find(port, "Window", 5, 0, IDENTIFIER.SIZINGTYPE).GetValue());
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void ThemeFilePointsAtTheInstalledStyle()
        {
            string t = XpThemeFiles.BuildTheme("Tango Blue", "TangoLux", "TangoLux Blue.msstyles", XpThemeFiles.DefaultWallpaper);
            StringAssert.Contains(t, "\r\nPath=%ResourceDir%\\Themes\\TangoLux\\TangoLux Blue.msstyles\r\n");
            StringAssert.Contains(t, "\r\nVisualStyleVersion=10\r\n");
            Assert.ThrowsException<ArgumentException>(() => XpThemeFiles.BuildTheme("a\r\n[x]", "f", "f.msstyles", "w"));
        }
    }
}
