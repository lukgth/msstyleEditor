using libmsstyle;
using libmsstyle.Import;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
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
        public void ResizeGripsRetainOwnNativeArtAcrossModernAlignments()
        {
            // Synthetic redrawn template: no dependency on a redistributable Win10 Luna port.
            // Exercise both consumers independently, including single-frame custom XP grips.
            foreach (int count in new[] { 1, 2 })
            using (var stock = new VisualStyle())
            using (var source = new VisualStyle())
            using (var target = new VisualStyle())
            {
                stock.Load(XpLuna); source.Load(XpLuna); target.Load(XpLuna);
                foreach (string cls in new[] { "Status", "ScrollBar" })
                {
                    int part = cls == "Status" ? 3 : 10;
                    var own = new ArgbImage(7 * count, 9);
                    for (int y = 0; y < own.Height; ++y)
                        for (int x = 0; x < own.Width; ++x)
                            own.Pixels[y * own.Width + x] = 0xFF000000u | (cls == "Status" ? 0x00800000u : 0x00008000u) | (uint)(y * own.Width + x);
                    SetXpGrip(source, cls, part, own, count, 1);
                    var redraw = new ArgbImage(16, 128);
                    for (int i = 0; i < redraw.Pixels.Length; ++i) redraw.Pixels[i] = 0xFF000000u;
                    SetXpGrip(target, cls, part, redraw, 8, 0);
                }
                var remapper = typeof(Win7StyleImporter).Assembly.GetType("libmsstyle.Import.XpStripRemap", true);
                var instance = Activator.CreateInstance(remapper, stock, source, target, new ImportReport());
                remapper.GetMethod("Run").Invoke(instance, null);
                foreach (string cls in new[] { "Status", "ScrollBar" })
                {
                    int part = cls == "Status" ? 3 : 10;
                    Assert.AreEqual(8, Find(source, cls, part, 0, IDENTIFIER.IMAGECOUNT).GetValue());
                    Assert.AreEqual(0, Find(source, cls, part, 0, IDENTIFIER.IMAGELAYOUT).GetValue());
                    var image = ImageIdentity.Decode(source.GetRawResource("IMAGE", Find(source, cls, part, 0, IDENTIFIER.IMAGEFILE).Header.shortFlag));
                    Assert.AreEqual(7, image.Width, "true-size glyph must not be enlarged to template dimensions");
                    Assert.AreEqual(72, image.Height);
                    for (int state = 0; state < 8; ++state)
                        for (int y = 0; y < 9; ++y)
                            for (int x = 0; x < 7; ++x)
                            {
                                bool left = (state & 1) != 0, top = (state & 2) != 0;
                                int sx = count == 1 && left ? 6 - x : x + (left && count == 2 ? 7 : 0);
                                int sy = top ? 8 - y : y;
                                uint expected = 0xFF000000u | (cls == "Status" ? 0x00800000u : 0x00008000u) | (uint)(sy * 7 * count + sx);
                                Assert.AreEqual(expected, image.Pixels[(state * 9 + y) * 7 + x], cls + " state " + (state + 1));
                            }
                }
            }
        }

        static void SetXpGrip(VisualStyle style, string cls, int part, ArgbImage image, int count, int layout)
        {
            var addImage = typeof(VisualStyle).GetMethod("AddXpImage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            int id = (int)addImage.Invoke(style, new object[] { ImageIdentity.EncodePngVerified(image) });
            Find(style, cls, part, 0, IDENTIFIER.IMAGEFILE).SetValue(id);
            Find(style, cls, part, 0, IDENTIFIER.IMAGECOUNT).SetValue(count);
            var clsInfo = style.Classes.Values.Single(c => c.ClassName.Equals(cls, StringComparison.OrdinalIgnoreCase));
            var layoutProp = Find(style, cls, part, 0, IDENTIFIER.IMAGELAYOUT);
            if (layoutProp == null)
            {
                layoutProp = new StyleProperty(new PropertyHeader((int)IDENTIFIER.IMAGELAYOUT, (int)IDENTIFIER.ENUM) { classID = clsInfo.ClassId, partID = part, stateID = 0 });
                clsInfo.Parts[part].States[0].Properties.Add(layoutProp);
            }
            layoutProp.SetValue(layout);
        }

        [TestMethod]
        public void StockLunaKeepsUnrelatedTemplateRecordsAndSynthesizesDwm()
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
            // Stock Luna's intentional structural and DWM conversions remain explicit;
            // no other decision is allowed to change a template record.
            var changes = r.Decisions.Where(d => d.Action.StartsWith("applied") || d.Action.StartsWith("added") || d.Action.StartsWith("copied") || d.Action.StartsWith("removed")).ToList();
            var dwm = changes.Where(d => d.Class == "DWMWindow").ToList();
            Assert.IsTrue(dwm.Any(d => d.Action == "applied-xp-frame-atlas"));
            Assert.IsTrue(dwm.Any(d => d.Action == "applied-xp-frame-property"));
            Assert.IsTrue(dwm.All(d => d.Action == "applied-xp-frame-atlas" || d.Action == "applied-xp-frame-property"));
            Assert.IsTrue(r.Dwm.Parts.Any(p => p.Method == "xp-frame"));
            Assert.IsTrue(r.Dwm.Parts.Any(p => p.Method == "xp-button"));
            Assert.IsTrue(r.Dwm.Parts.All(p => p.NewRect == p.TargetRect), "XP synthesis must not relocate any imported or kept sprite");
            Assert.IsTrue(r.Validation.Checks.Any(c => c.Contains("DWMWindow sprites verified pixel-for-pixel")), string.Join("\n", r.Validation.Checks));
            changes.RemoveAll(d => d.Class == "DWMWindow");
            // A stock source states no intent beyond stock xp, so nothing it says may delete a template record.
            var removals = changes.Where(d => d.Action.StartsWith("removed")).ToList();
            Assert.AreEqual(0, removals.Count, "porting stock xp luna must not remove template records:\n"
                + string.Join("\n", removals.Select(d => $"{d.Class}|{d.Part}|{d.State}|{d.Property}")));
            // Every remaining change is one of the xp rules, each of which states why it fires. A key whitelist can't
            // stay honest here: stock luna legitimately adds the ~390 records the win10 template never had and fills
            // the template-only dpi slots, so the guard is that no mutation happens under an unrecognised rule.
            var known = new HashSet<string>
            {
                "added",                        // the template has no such record; the source's is carried over
                "applied-dpi-fill",             // template-only dpi slot gets the source's art
                "applied-dpi-threshold",        // MINDPIn follows the dpi slot that was filled
                "applied-shellstyle",           // the shellstyle's own geometry
                "applied-stock-over-template",  // prefer-source: the source kept stock art the template redrew
                "applied-xp-header-margins",    // ExplorerBar 8/12 CONTENTMARGINS from the source style
                "applied-xp-startpanel-counterpart", // StartPanel geometry win10 splits across classes
                "applied-marquee-overlay",      // win10's indeterminate bar gets the source's chunk art (xp has no marquee bar of its own)
            };
            var unknown = changes.Where(d => !known.Contains(d.Action)).ToList();
            Assert.AreEqual(0, unknown.Count, "unrecognised import rule fired:\n"
                + string.Join("\n", unknown.Select(d => $"{d.Class}|{d.Part}|{d.State}|{d.Property}: {d.Action}")));
            // the structural conversions the test exists for: xp's rebar band and menu bar become win10 records.
            var structural = new[] { "Rebar|BAND|0|BGTYPE", "Rebar|BAND|0|IMAGEFILE", "Rebar|BAND|0|IMAGECOUNT", "Rebar|BAND|0|SIZINGTYPE", "Rebar|BAND|0|SIZINGMARGINS",
                                     "Menu|BARBACKGROUND|0|BGTYPE", "Menu|BARBACKGROUND|0|IMAGEFILE", "Menu|BARBACKGROUND|0|IMAGECOUNT", "Menu|BARBACKGROUND|0|SIZINGTYPE", "Menu|BARBACKGROUND|0|SIZINGMARGINS" };
            var keys = changes.Select(d => $"{d.Class}|{d.Part}|{d.State}|{d.Property}").ToList();
            foreach (var key in structural)
                Assert.IsTrue(keys.Contains(key), "the xp rebar/menu structural conversion is missing: " + key);
            // DWM artwork is synthesized separately; these remain the generic structural changes.
            Assert.IsTrue(r.Validation.Checks.Any(c => c.Contains("template resources are byte-identical")), string.Join("\n", r.Validation.Checks));
            // the port re-cut Toolbar BUTTON from 6 to 8 frames with stock frames; that mapping must be learned.
            Assert.IsTrue(r.Notes.Any(n => n.StartsWith("Toolbar part ") && n.Contains(": strip re-cut from 6 vertical to 8 vertical")), string.Join("\n", r.Notes));
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
            // The address box draws its own combo image; pane padding follows shellstyle.
            // Explicit source header CONTENTMARGINS takes precedence over shellstyle inset deltas.
            Assert.IsTrue(r.Decisions.Any(d => d.Class == "Combobox" && d.Part == "Common Properties" && d.State == 0 && d.Property == "BGTYPE" && d.Action == "added"));
            Assert.AreEqual("(l7,t7,r7,b7)", r.Decisions.Single(d => d.Class == "ExplorerBar" && d.Part == "Common Properties" && d.Property == "CONTENTMARGINS" && d.Action == "applied-shellstyle").Result);
            using (var xp = new VisualStyle())
            {
                xp.Load(TangoLux);
                var explicitMargin = Find(xp, "ExplorerBar", 12, 0, IDENTIFIER.CONTENTMARGINS);
                var header = r.Decisions.Where(d => d.Class == "ExplorerBar" && d.Part == "SPECIALGROUPHEAD" && d.Property == "CONTENTMARGINS").ToList();
                Assert.IsTrue(header.Count > 0);
                Assert.IsTrue(header.All(d => d.Result == (explicitMargin == null ? "(l10,t0,r3,b0)" : SemanticModel.FormatValue(explicitMargin))));
            }
            // xp tiled the progress chunk art; Luna10 set STRETCH for its own pill. Now that the source's picture and
            // margins are in the part, xp's TILE goes with them or the 18x18 hatch smears across the whole bar.
            var chunk = r.Decisions.Single(d => d.Class == "Progress" && d.Part == "CHUNK" && d.State == 0 && d.Property == "SIZINGTYPE");
            Assert.AreEqual("applied-xp-sizingtype", chunk.Action);
            Assert.AreEqual("2", chunk.Result); // final raw SIZINGTYPE value (TILE), not the intermediate display label
        }

        [TestMethod]
        public void IndeterminateProgressBarDrawsTheSourceChunkArt()
        {
            if (Luna10 == null || !File.Exists(Luna10)) Assert.Inconclusive("set MSSTYLE_LUNA10 to the win10 Luna.msstyles port");
            string dir = Path.Combine(Path.GetTempPath(), "msstyle-marquee-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string output = Path.Combine(dir, "luna.msstyles");
                var r = Win7StyleImporter.Run(new Win7ImportOptions
                {
                    SourcePath = XpLuna, SourcePlatform = Platform.WinXP, SourceScheme = "NormalColor",
                    BaseScheme = "NormalColor", SourceShellStylePath = StockStyles.XpLunaShellPath("NormalColor"),
                    TemplatePath = Luna10, OutputPath = output, DryRun = false,
                });
                Assert.AreEqual(ImportStatus.ReadyForVmTest, r.Status, string.Join("\n", r.Errors));
                // xp has no indeterminate bar; win10's PULSEOVERLAY/MOVEOVERLAY were left blank by the merge, so a
                // marquee bar (wer's "checking for a solution") fell back to comctl32's classic green blocks. Both
                // moving overlays must draw the source's chunk art instead.
                using (var xp = new VisualStyle())
                using (var port = new VisualStyle())
                {
                    xp.Load(XpLuna);
                    port.Load(output);
                    foreach (var (target, source) in new[] { (8, 3), (10, 4) })
                    {
                        Assert.AreEqual(0, Find(port, "Progress", target, 0, IDENTIFIER.BGTYPE).GetValue(), "overlay " + target + " must draw its picture");
                        int srcId = Find(xp, "Progress", source, 0, IDENTIFIER.IMAGEFILE).Header.shortFlag & 0xFFFF;
                        int dstId = Find(port, "Progress", target, 0, IDENTIFIER.IMAGEFILE).Header.shortFlag & 0xFFFF;
                        Assert.AreEqual(ImageIdentity.Decode(xp.GetRawResource("IMAGE", srcId)).PixelSha1(),
                            ImageIdentity.Decode(port.GetRawResource("IMAGE", dstId)).PixelSha1(),
                            "overlay " + target + " must draw the chunk art");
                        foreach (var id in new[] { IDENTIFIER.SIZINGMARGINS, IDENTIFIER.IMAGELAYOUT })
                            Assert.AreEqual(Find(xp, "Progress", source, 0, id).GetValue(), Find(port, "Progress", target, 0, id).GetValue(), "strip geometry changed: " + id);
                    }
                }
            }
            finally { Directory.Delete(dir, true); }
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
                    foreach (string cls in new[] { "Status", "ScrollBar" })
                    {
                        int part = cls == "Status" ? 3 : 10;
                        var xpClass = xp.Classes.Values.Single(c => c.ClassName.Equals(cls, StringComparison.OrdinalIgnoreCase));
                        var original = ImageIdentity.Decode(xp.GetRawResource("IMAGE", Find(xp, cls, part, 0, IDENTIFIER.IMAGEFILE).Header.shortFlag));
                        int count = (int)Find(xp, cls, part, 0, IDENTIFIER.IMAGECOUNT).GetValue();
                        bool horizontal = (int)SemanticModel.FindEffective(xp, xpClass, part, 0, IDENTIFIER.IMAGELAYOUT).GetValue() == 1;
                        int w = horizontal ? original.Width / count : original.Width, h = horizontal ? original.Height : original.Height / count;
                        Assert.AreEqual(8, Find(port, cls, part, 0, IDENTIFIER.IMAGECOUNT).GetValue());
                        Assert.AreEqual(0, Find(port, cls, part, 0, IDENTIFIER.IMAGELAYOUT).GetValue());
                        foreach (var slot in new[] { IDENTIFIER.IMAGEFILE1, IDENTIFIER.IMAGEFILE2, IDENTIFIER.IMAGEFILE3, IDENTIFIER.IMAGEFILE4, IDENTIFIER.IMAGEFILE5, IDENTIFIER.IMAGEFILE6, IDENTIFIER.IMAGEFILE7 })
                        {
                            var image = ImageIdentity.Decode(port.GetRawResource("IMAGE", Find(port, cls, part, 0, slot).Header.shortFlag));
                            Assert.AreEqual(w, image.Width, cls + " " + slot);
                            Assert.AreEqual(h * 8, image.Height, cls + " " + slot);
                            for (int state = 0; state < 8; ++state)
                                for (int y = 0; y < h; ++y)
                                    for (int x = 0; x < w; ++x)
                                    {
                                        int frame = state % 2, sy = (state & 2) != 0 ? h - y - 1 : y;
                                        int sourceIndex = (horizontal ? sy : frame * h + sy) * original.Width + (horizontal ? frame * w + x : x);
                                        Assert.AreEqual(original.Pixels[sourceIndex], image.Pixels[(state * h + y) * w + x], cls + " " + slot + " state " + (state + 1));
                                    }
                        }
                    }
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void CustomStartPanelKeepsSourcePaneSizesAndArtwork()
        {
            string source = Environment.GetEnvironmentVariable("MSSTYLE_XP_SEVENVG");
            if (Luna10 == null || !File.Exists(Luna10)) Assert.Inconclusive("set MSSTYLE_LUNA10 to the win10 Luna.msstyles port");
            if (source == null || !File.Exists(source)) Assert.Inconclusive("set MSSTYLE_XP_SEVENVG to SevenVG RTM.msstyles");
            string dir = Path.Combine(Path.GetTempPath(), "msstyle-start-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string output = Path.Combine(dir, "SevenVG.msstyles");
                var report = Win7StyleImporter.Run(new Win7ImportOptions
                {
                    SourcePath = source, SourcePlatform = Platform.WinXP, SourceScheme = "NormalColor",
                    BaseScheme = "NormalColor", TemplatePath = Luna10, OutputPath = output, DryRun = false,
                });
                Assert.AreEqual(ImportStatus.ReadyForVmTest, report.Status, string.Join("\n", report.Errors));
                using (var xp = new VisualStyle())
                using (var port = new VisualStyle())
                {
                    xp.Load(source); port.Load(output);
                    foreach (int part in new[] { 0, 1, 2, 4, 6, 8 })
                        Assert.AreEqual(Find(xp, "StartPanel", part, 0, IDENTIFIER.DEFAULTPANESIZE).GetValue(),
                            Find(port, "StartPanel", part, 0, IDENTIFIER.DEFAULTPANESIZE).GetValue(), "pane dimensions changed for " + part);
                    foreach (var size in report.FinalStartPanelSizes.Where(s => s.Class == "StartPanel"))
                        Assert.AreEqual(SemanticModel.FormatValue(Find(port, "StartPanel", size.PartId, 0, IDENTIFIER.DEFAULTPANESIZE)), size.Value,
                            "reported pane size is not the final output for " + size.PartId);
                    foreach (var decision in report.Decisions.Where(d => d.Class == "StartPanel" && d.Property == "DEFAULTPANESIZE"))
                    {
                        var size = report.FinalStartPanelSizes.SingleOrDefault(s => s.Class == "StartPanel" && s.Part == decision.Part);
                        if (size != null) Assert.AreEqual(size.Value, decision.Result, "intermediate decision result leaked into the report");
                    }
                    foreach (int part in new[] { 1, 2, 4, 6, 8, 9, 10 })
                    {
                        int xpId = Find(xp, "StartPanel", part, 0, IDENTIFIER.IMAGEFILE).Header.shortFlag & 0xFFFF;
                        int portId = Find(port, "StartPanel", part, 0, IDENTIFIER.IMAGEFILE).Header.shortFlag & 0xFFFF;
                        Assert.AreEqual(ImageIdentity.Decode(xp.GetRawResource("IMAGE", xpId)).PixelSha1(),
                            ImageIdentity.Decode(port.GetRawResource("IMAGE", portId)).PixelSha1(), "Start artwork changed for " + part);
                    }
                    var user = (Margins)Find(port, "StartPanel", 1, 0, IDENTIFIER.DEFAULTPANESIZE).GetValue();
                    var programs = (Margins)Find(port, "StartPanel", 4, 0, IDENTIFIER.DEFAULTPANESIZE).GetValue();
                    var places = (Margins)Find(port, "StartPanel", 6, 0, IDENTIFIER.DEFAULTPANESIZE).GetValue();
                    Assert.AreEqual(places.Right - places.Left, (user.Right - user.Left) - (programs.Right - programs.Left),
                        "the actual places column must retain its declared width");
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void PropagatedRebarBackdropsKeepSourceRowsAndValidMargins()
        {
            string source = Environment.GetEnvironmentVariable("MSSTYLE_XP_SEVENVG");
            if (Luna10 == null || !File.Exists(Luna10)) Assert.Inconclusive("set MSSTYLE_LUNA10 to the win10 Luna.msstyles port");
            if (source == null || !File.Exists(source)) Assert.Inconclusive("set MSSTYLE_XP_SEVENVG to SevenVG RTM.msstyles");
            string dir = Path.Combine(Path.GetTempPath(), "msstyle-band-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string output = Path.Combine(dir, "SevenVG.msstyles");
                var report = Win7StyleImporter.Run(new Win7ImportOptions
                {
                    SourcePath = source, SourcePlatform = Platform.WinXP, SourceScheme = "NormalColor",
                    BaseScheme = "NormalColor", TemplatePath = Luna10, OutputPath = output, DryRun = false,
                });
                Assert.AreEqual(ImportStatus.ReadyForVmTest, report.Status, string.Join("\n", report.Errors));
                using (var xp = new VisualStyle())
                using (var port = new VisualStyle())
                {
                    xp.Load(source); port.Load(output);
                    var strip = ImageIdentity.Decode(xp.GetRawResource("IMAGE", Find(xp, "Rebar", 0, 0, IDENTIFIER.IMAGEFILE).Header.shortFlag));
                    var margins = (Margins)Find(xp, "Rebar", 0, 0, IDENTIFIER.SIZINGMARGINS).GetValue();
                    foreach (string cls in new[] { "CommandModule", "CPLCommandModule::CommandModule" })
                    {
                        var image = ImageIdentity.Decode(port.GetRawResource("IMAGE", Find(port, cls, 1, 0, IDENTIFIER.IMAGEFILE).Header.shortFlag));
                        var actual = (Margins)Find(port, cls, 1, 0, IDENTIFIER.SIZINGMARGINS).GetValue();
                        Assert.AreEqual(margins, actual, cls + " inherited template margins instead of source geometry");
                        Assert.IsTrue(actual.Left + actual.Right < image.Width && actual.Top + actual.Bottom < image.Height,
                            cls + " has no stretchable center");
                        Assert.AreEqual(strip.Height, image.Height, cls + " resampled the source gradient");
                        for (int y = 0; y < image.Height; ++y)
                            for (int x = 0; x < image.Width; ++x)
                                Assert.AreEqual(strip.Pixels[y], image.Pixels[y * image.Width + x], cls + " source row " + y);
                    }
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

        [TestMethod]
        public void ComboButtonSplitsFaceAndArrow()
        {
            string source = Environment.GetEnvironmentVariable("MSSTYLE_XP_GLITCHBLACK");
            if (Luna10 == null || !File.Exists(Luna10)) Assert.Inconclusive("set MSSTYLE_LUNA10 to the win10 Luna.msstyles port");
            if (source == null || !File.Exists(source)) Assert.Inconclusive("set MSSTYLE_XP_GLITCHBLACK to GlitchBlack.msstyles");
            string dir = Path.Combine(Path.GetTempPath(), "msstyle-combo-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string output = Path.Combine(dir, "GlitchBlack.msstyles");
                var report = Win7StyleImporter.Run(new Win7ImportOptions
                {
                    SourcePath = source, SourcePlatform = Platform.WinXP, SourceScheme = "NormalColor",
                    BaseScheme = "NormalColor", TemplatePath = Luna10, OutputPath = output, DryRun = false,
                });
                Assert.AreEqual(ImportStatus.ReadyForVmTest, report.Status, string.Join("\n", report.Errors));
                using (var xp = new VisualStyle())
                using (var tpl = new VisualStyle())
                using (var port = new VisualStyle())
                {
                    xp.Load(source); tpl.Load(Luna10); port.Load(output);
                    // the source draws the button from a plate (IMAGEFILE) under a separate arrow (GLYPHIMAGEFILE),
                    // and the template's own button is the same two layers. The two must travel separately: the
                    // face into IMAGEFILE, the arrow into every dpi slot, or the arrow scales with the nine-sliced
                    // face (at the address bar's narrow button xp's 9px arrow came out 5px wide).
                    int faceId = Find(xp, "Combobox", 1, 0, IDENTIFIER.IMAGEFILE).Header.shortFlag & 0xFFFF;
                    int glyphId = Find(xp, "Combobox", 1, 0, IDENTIFIER.GLYPHIMAGEFILE).Header.shortFlag & 0xFFFF;
                    var face = ImageIdentity.Decode(xp.GetRawResource("IMAGE", faceId));
                    var glyph = ImageIdentity.Decode(xp.GetRawResource("IMAGE", glyphId));
                    Assert.AreNotEqual(face.PixelSha1(), glyph.PixelSha1(), "the fixture's face and arrow are the same picture");
                    foreach (int part in new[] { 1, 6, 7 })
                    {
                        var faceProp = Find(port, "Combobox", part, 0, IDENTIFIER.IMAGEFILE);
                        Assert.IsNotNull(faceProp, "part " + part + " lost IMAGEFILE");
                        var faceImg = ImageIdentity.Decode(port.GetRawResource("IMAGE", faceProp.Header.shortFlag & 0xFFFF));
                        for (int name = (int)IDENTIFIER.IMAGEFILE1; name <= (int)IDENTIFIER.IMAGEFILE7; ++name)
                        {
                            if (Find(tpl, "Combobox", part, 0, (IDENTIFIER)name) == null) continue;
                            var p = Find(port, "Combobox", part, 0, (IDENTIFIER)name);
                            Assert.IsNotNull(p, "part " + part + " lost " + (IDENTIFIER)name);
                            var img = ImageIdentity.Decode(port.GetRawResource("IMAGE", p.Header.shortFlag & 0xFFFF));
                            Assert.AreEqual(glyph.PixelSha1(), img.PixelSha1(),
                                "part " + part + " " + (IDENTIFIER)name + " does not hold the source's arrow");
                            Assert.AreNotEqual(faceImg.PixelSha1(), img.PixelSha1(),
                                "part " + part + " " + (IDENTIFIER)name + " holds the face, not the arrow");
                        }
                        // the arrow now lives in the dpi slots, so both glyph switches must be on for win10 to draw
                        // it (GLYPHTYPE IMAGEGLYPH makes it the glyph; IMAGESELECTTYPE DPI picks the slot).
                        var gt = Find(port, "Combobox", part, 0, IDENTIFIER.GLYPHTYPE);
                        Assert.IsNotNull(gt, "part " + part + " lost GLYPHTYPE, so the arrow in the slots is never drawn");
                        Assert.AreEqual(1, (int)gt.GetValue(), "part " + part + " does not draw an image glyph");
                        var sel = Find(port, "Combobox", part, 0, IDENTIFIER.IMAGESELECTTYPE);
                        Assert.IsNotNull(sel, "part " + part + " lost the dpi-selection switch");
                        Assert.AreEqual(2, (int)sel.GetValue(), "part " + part + " does not select the arrow by dpi");
                        // GLYPHIMAGEFILE is dead while the arrow is selected by dpi, but if it survived from the
                        // source it must be the arrow, never the face.
                        var gim = Find(port, "Combobox", part, 0, IDENTIFIER.GLYPHIMAGEFILE);
                        if (gim != null)
                        {
                            Assert.AreEqual(glyph.PixelSha1(),
                                ImageIdentity.Decode(port.GetRawResource("IMAGE", gim.Header.shortFlag & 0xFFFF)).PixelSha1(),
                                "part " + part + " GLYPHIMAGEFILE is not xp's arrow");
                        }
                    }
                    Assert.IsTrue(report.Decisions.Any(d => d.Action == "split-xp-combo-button"));
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }
    }
}
