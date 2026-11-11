using libmsstyle;
using libmsstyle.Import;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;

namespace libmsstyleTests
{
    [TestClass]
    public class FidelityTests
    {
        const string Luna = @"..\..\..\styles\xp_luna\luna.msstyles";
        const BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;

        static StyleClass Class(VisualStyle style, string name) => style.Classes.Values.Single(c => c.ClassName.Equals(name, StringComparison.OrdinalIgnoreCase));
        static StyleProperty Property(VisualStyle style, string name, int part, IDENTIFIER id) => Class(style, name).Parts[part].States[0].Properties.FirstOrDefault(p => p.Header.nameID == (int)id);
        static ArgbImage Pattern(int count, bool horizontal, uint seed)
        {
            var image = new ArgbImage(horizontal ? count * 3 : 3, horizontal ? 3 : count * 3);
            for (int frame = 0; frame < count; ++frame)
                for (int y = 0; y < 3; ++y)
                    for (int x = 0; x < 3; ++x)
                        image.Pixels[(horizontal ? y : frame * 3 + y) * image.Width + (horizontal ? frame * 3 + x : x)] = seed + (uint)(frame * 0x080808 + y * 3 + x);
            return image;
        }
        static void SetStrip(VisualStyle style, string cls, int part, int count, bool horizontal, ArgbImage image)
        {
            var c = Class(style, cls);
            var state = c.Parts[part].States[0];
            state.Properties.RemoveAll(p => p.Header.nameID >= (int)IDENTIFIER.IMAGEFILE1 && p.Header.nameID <= (int)IDENTIFIER.IMAGEFILE5 || p.Header.nameID == (int)IDENTIFIER.GLYPHIMAGEFILE);
            int id = (int)typeof(VisualStyle).GetMethod("AddXpImage", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(style, new object[] { ImageIdentity.EncodePngVerified(image) });
            Property(style, cls, part, IDENTIFIER.IMAGEFILE).SetValue(id);
            foreach (var item in new[] { Tuple.Create(IDENTIFIER.IMAGECOUNT, IDENTIFIER.INT, count), Tuple.Create(IDENTIFIER.IMAGELAYOUT, IDENTIFIER.ENUM, horizontal ? 1 : 0) })
            {
                var p = Property(style, cls, part, item.Item1);
                if (p == null)
                {
                    p = new StyleProperty(new PropertyHeader((int)item.Item1, (int)item.Item2) { classID = c.ClassId, partID = part, stateID = 0 });
                    state.Properties.Add(p);
                }
                p.SetValue(item.Item3);
            }
            // Run only this class/part: unrelated fixture art cannot determine these assertions.
            foreach (var other in style.Classes.Keys.Where(key => key != c.ClassId).ToList()) style.Classes.Remove(other);
            foreach (var other in c.Parts.Keys.Where(key => key != part).ToList()) c.Parts.Remove(other);
        }
        static ImportReport Remap(VisualStyle stock, VisualStyle source, VisualStyle target)
        {
            var report = new ImportReport();
            var type = typeof(Win7StyleImporter).Assembly.GetType("libmsstyle.Import.XpStripRemap", true);
            type.GetMethod("Run").Invoke(Activator.CreateInstance(type, stock, source, target, report), null);
            return report;
        }
        static ArgbImage Strip(VisualStyle style, string cls, int part) => ImageIdentity.Decode(style.GetRawResource("IMAGE", Property(style, cls, part, IDENTIFIER.IMAGEFILE).Header.shortFlag & 0xFFFF));

        [TestMethod]
        public void SixFramePushbuttonPreservesItsOwnAnimatingFrame()
        {
            foreach (bool horizontal in new[] { false, true })
            using (var stock = new VisualStyle())
            using (var source = new VisualStyle())
            using (var target = new VisualStyle())
            {
                stock.Load(Luna); source.Load(Luna); target.Load(Luna);
                SetStrip(stock, "Button", 1, 5, false, Pattern(5, false, 0xFF000000));
                var own = Pattern(6, horizontal, 0xFF100000);
                SetStrip(source, "Button", 1, 6, horizontal, own);
                SetStrip(target, "Button", 1, 6, false, Pattern(6, false, 0xFF001000));
                var report = Remap(stock, source, target);
                Assert.IsFalse(report.UnmappedStripParts.Contains("Button|1"));
                Assert.AreEqual(0, report.Warnings.Count, string.Join("\n", report.Warnings));
                CollectionAssert.AreEqual(Pattern(6, false, 0xFF100000).Pixels, Strip(source, "Button", 1).Pixels);
                Assert.AreEqual(6, Property(source, "Button", 1, IDENTIFIER.IMAGECOUNT).GetValue());
            }
        }

        [TestMethod]
        public void ScrollbarHoverUsesNormalNotHotFrame()
        {
            foreach (int part in new[] { 1, 2, 8, 9 })
            using (var stock = new VisualStyle())
            using (var source = new VisualStyle())
            using (var target = new VisualStyle())
            {
                stock.Load(Luna); source.Load(Luna); target.Load(Luna);
                int oldCount = part == 1 ? 16 : 4, newCount = part == 1 ? 20 : 5;
                SetStrip(stock, "ScrollBar", part, oldCount, false, Pattern(oldCount, false, 0xFF000000));
                SetStrip(source, "ScrollBar", part, oldCount, true, Pattern(oldCount, true, 0xFF100000));
                SetStrip(target, "ScrollBar", part, newCount, false, Pattern(newCount, false, 0xFF001000));
                var report = Remap(stock, source, target);
                Assert.IsFalse(report.UnmappedStripParts.Contains("ScrollBar|" + part));
                var actual = Strip(source, "ScrollBar", part);
                for (int j = oldCount; j < newCount; ++j)
                {
                    int expectedFrame = part == 1 ? (j - oldCount) * 4 : 0;
                    Assert.AreEqual(0xFF100000u + (uint)(expectedFrame * 0x080808), actual.Pixels[j * 9]);
                }
            }
        }

        [TestMethod]
        public void UnknownRedrawnStateNeverUsesNearestFrame()
        {
            using (var stock = new VisualStyle())
            using (var source = new VisualStyle())
            using (var target = new VisualStyle())
            {
                stock.Load(Luna); source.Load(Luna); target.Load(Luna);
                SetStrip(stock, "Toolbar", 1, 6, false, Pattern(6, false, 0xFF000000));
                SetStrip(source, "Toolbar", 1, 6, false, Pattern(6, false, 0xFF100000));
                var redraw = Pattern(8, false, 0xFF000000);
                for (int i = 6 * 9; i < redraw.Pixels.Length; ++i) redraw.Pixels[i] = 0xFFFFFFFF;
                SetStrip(target, "Toolbar", 1, 8, false, redraw);
                string before = Strip(source, "Toolbar", 1).PixelSha1();
                var report = Remap(stock, source, target);
                Assert.IsTrue(report.UnmappedStripParts.Contains("Toolbar|1"));
                Assert.IsTrue(report.Warnings.Any(w => w.Contains("locked to the template")));
                Assert.AreEqual(before, Strip(source, "Toolbar", 1).PixelSha1());
            }
        }

        [TestMethod]
        public void SourceOnlyUnknownFrameCountLocksEvenWhenStockAndTemplateMatch()
        {
            using (var stock = new VisualStyle())
            using (var source = new VisualStyle())
            using (var target = new VisualStyle())
            {
                stock.Load(Luna); source.Load(Luna); target.Load(Luna);
                SetStrip(stock, "Toolbar", 1, 6, false, Pattern(6, false, 0xFF100000));
                SetStrip(source, "Toolbar", 1, 7, false, Pattern(7, false, 0xFF200000));
                SetStrip(target, "Toolbar", 1, 6, false, Pattern(6, false, 0xFF100000));
                var report = Remap(stock, source, target);
                Assert.IsTrue(report.UnmappedStripParts.Contains("Toolbar|1"));
                Assert.AreEqual(7, Property(source, "Toolbar", 1, IDENTIFIER.IMAGECOUNT).GetValue());
                Assert.IsTrue(report.Warnings.Any(w => w.Contains("no semantic mapping")));
            }
        }

        [TestMethod]
        public void ReportFinalValuesFollowLateChangesAndResourceReferences()
        {
            using (var style = new VisualStyle())
            {
                style.Load(Luna);
                var size = Property(style, "StartPanel", 1, IDENTIFIER.DEFAULTPANESIZE);
                size.SetValue(new Margins(0, 0, 375, 90));
                int imageId = Property(style, "StartPanel", 1, IDENTIFIER.IMAGEFILE).Header.shortFlag & 0xFFFF;
                var report = new ImportReport();
                report.Decisions.Add(new MergeDecision { Class = "StartPanel", Part = "USERPANE", State = 0, Property = "DEFAULTPANESIZE", Action = "applied", Result = "stale" });
                report.Resources.Add(new ResourceMapping { Type = "IMAGE", SourceId = 1, TargetId = imageId });
                report.Resources.Add(new ResourceMapping { Type = "IMAGE", SourceId = 2, TargetId = 65000 });
                report.FinalizeResults(style);
                Assert.AreEqual(SemanticModel.FormatValue(size), report.Decisions[0].Result);
                Assert.AreEqual(SemanticModel.FormatValue(size), report.FinalStartPanelSizes.Single(s => s.Class == "StartPanel" && s.PartId == 1).Value);
                Assert.IsTrue(report.Resources[0].Referenced);
                Assert.IsFalse(report.Resources[1].Referenced);
                StringAssert.Contains(ImportReportWriter.ToMarkdown(report), "Final StartPanel pane sizes");
                StringAssert.Contains(ImportReportWriter.ToMarkdown(report), "final references");
            }
        }

        [TestMethod]
        public void XpFrameSynthesisPreservesStateBordersAndPadding()
        {
            var method = typeof(Win7StyleImporter).Assembly.GetType("libmsstyle.Import.DwmImport", true).GetMethod("SynthesizeXpFrameStrip", HiddenStatic);
            foreach (bool horizontal in new[] { false, true })
            {
                var source = Pattern(2, horizontal, 0xFF100000);
                object[] args = { source, 2, horizontal, 7, 14, 2, new Margins(1, 1, 1, 1), false, null };
                var actual = (ArgbImage)method.Invoke(null, args);
                Assert.AreEqual(new Margins(1, 1, 1, 1), args[8]);
                for (int state = 0; state < 2; ++state)
                {
                    int top = state * 7;
                    uint first = 0xFF100000u + (uint)(state * 0x080808);
                    Assert.AreEqual(first, actual.Pixels[top * 7]);
                    Assert.AreEqual(first + 8, actual.Pixels[(top + 6) * 7 + 6]);
                    for (int x = 0; x < 7; ++x)
                    {
                        Assert.AreEqual(actual.Pixels[(top + 1) * 7 + x], actual.Pixels[top * 7 + x]);
                        Assert.AreEqual(actual.Pixels[(top + 5) * 7 + x], actual.Pixels[(top + 6) * 7 + x]);
                    }
                }
                object[] small = { source, 2, horizontal, 3, 6, 2, new Margins(50, 50, 50, 50), false, null };
                Assert.IsNotNull(method.Invoke(null, small));
                Assert.AreEqual(new Margins(0, 0, 0, 0), small[8]);
            }
        }

        [TestMethod]
        public void ThemeWithoutExtrasDoesNotInventIconsMetricsOrDarkMode()
        {
            string text = XpThemeFiles.BuildTheme("Probe", "Probe", "Probe.msstyles", XpThemeFiles.DefaultWallpaper);
            StringAssert.Contains(text, "AppMode=Light");
            StringAssert.Contains(text, "SystemMode=Light");
            Assert.IsFalse(text.Contains("imageres.dll"));
            Assert.IsFalse(text.Contains("[Metrics]"));
            var keys = (string[])typeof(XpThemeFiles).GetField("CursorKeys", HiddenStatic).GetValue(null);
            foreach (string key in keys) StringAssert.Contains(text, key + "=\r\n");
        }
        static Win7StyleImporter Importer(VisualStyle stock, VisualStyle source, VisualStyle target)
        {
            var importer = new Win7StyleImporter(new Win7ImportOptions { SourcePlatform = Platform.WinXP });
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(Win7StyleImporter).GetField("m_base", fields).SetValue(importer, new SemanticModel(stock));
            typeof(Win7StyleImporter).GetField("m_source", fields).SetValue(importer, new SemanticModel(source));
            typeof(Win7StyleImporter).GetField("m_target", fields).SetValue(importer, new SemanticModel(target));
            var existing = target.Classes.Values.SelectMany(c => c.Parts.Values).SelectMany(p => p.States.Values).SelectMany(s => s.Properties)
                .Select(p => SemanticModel.ResourceRef(p, target.Platform)).Where(r => r != null)
                .Select(r => Tuple.Create(r.Item1, r.Item2, (string)null));
            typeof(Win7StyleImporter).GetField("m_alloc", fields).SetValue(importer, new ResourceIdAllocator(existing));
            return importer;
        }
        static object Invoke(Win7StyleImporter importer, string name, params object[] arguments) =>
            typeof(Win7StyleImporter).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(importer, arguments);

        [TestMethod]
        public void MissingFontCanBeAddedAndExplorerToolbarAppendPreservesExistingIds()
        {
            using (var stock = new VisualStyle())
            using (var source = new VisualStyle())
            using (var target = new VisualStyle())
            {
                stock.Load(Luna); source.Load(Luna); target.Load(@"..\..\..\styles\aero11_7_clear.msstyles");
                var sourceClass = Class(source, "ExplorerBar");
                var font = new StyleProperty(new PropertyHeader((int)IDENTIFIER.FONT, (int)IDENTIFIER.FONT)
                    { classID = sourceClass.ClassId, partID = 12, stateID = 0 });
                int sid = source.PreferredStringTable.Keys.Max() + 1;
                source.PreferredStringTable[sid] = "Tahoma, 11, bold";
                font.SetValue(sid);
                sourceClass.Parts[12].States[0].Properties.RemoveAll(p => p.Header.nameID == (int)IDENTIFIER.FONT);
                sourceClass.Parts[12].States[0].Properties.Add(font);
                Class(target, "ExplorerBar").Parts[12].States[0].Properties.RemoveAll(p => p.Header.nameID == (int)IDENTIFIER.FONT);
                if (!source.Classes.Values.Any(c => c.ClassName == "ExplorerBar::Toolbar"))
                {
                    int id = source.Classes.Keys.Max() + 1;
                    source.Classes.Add(id, new StyleClass { ClassId = id, ClassName = "ExplorerBar::Toolbar" });
                }
                var original = target.Classes.ToDictionary(c => c.Key, c => c.Value.ClassName);
                var importer = Importer(stock, source, target);
                var entry = new SemanticModel(source).Entries.Single(e => ReferenceEquals(e.Property, font));
                object[] args = { entry, new SemanticModel(source), null };
                Assert.IsTrue((bool)Invoke(importer, "TryAddRecord", args), Convert.ToString(args[2]));
                var outputFont = Property(target, "ExplorerBar", 12, IDENTIFIER.FONT);
                Assert.AreEqual("Tahoma, 11, Bold", new SemanticModel(target).ResolveString(outputFont.Header.shortFlag));
                Invoke(importer, "AppendExplorerToolbar");
                foreach (var cls in original) Assert.AreEqual(cls.Value, target.Classes[cls.Key].ClassName);
                var appended = Class(target, "ExplorerBar::Toolbar");
                Assert.IsTrue(appended.ClassId > original.Keys.Max());
                Assert.AreEqual(Class(target, "Toolbar").ClassId, appended.BaseClassId);
                string candidate = Path.Combine(Path.GetTempPath(), "xp-append-test-" + Guid.NewGuid().ToString("N") + ".msstyles");
                try
                {
                    var part = new StylePart { PartId = 0, PartName = "Common Properties" };
                    var state = new StyleState { StateId = 0, StateName = "Common Properties" };
                    var record = new StyleProperty(new PropertyHeader((int)IDENTIFIER.BGTYPE, (int)IDENTIFIER.ENUM)
                        { classID = appended.ClassId, partID = 0, stateID = 0 });
                    record.SetValue(0); state.Properties.Add(record); part.States.Add(0, state); appended.Parts.Add(0, part);
                    target.Save(candidate);
                    using (var originalMaps = new ResourceInventory(@"..\..\..\styles\aero11_7_clear.msstyles"))
                    {
                        Invoke(importer, "RewriteAppendedClassMaps", candidate, originalMaps, target);
                        using (var saved = new ResourceInventory(candidate))
                            foreach (var mapEntry in originalMaps.Entries.Where(e => e.Type == "CMAP"))
                            {
                                byte[] before = originalMaps.GetData(mapEntry), after = saved.GetData(saved.Entries.Single(e => e.Key == mapEntry.Key));
                                CollectionAssert.AreEqual(before, after.Take(before.Length).ToArray(), "existing CMAP bytes must not move");
                            }
                    }
                    using (var saved = new VisualStyle())
                    {
                        saved.Load(candidate);
                        foreach (var cls in original) Assert.AreEqual(cls.Value, saved.Classes[cls.Key].ClassName);
                        Assert.AreEqual(Class(saved, "Toolbar").ClassId, Class(saved, "ExplorerBar::Toolbar").BaseClassId);
                        foreach (var cls in target.Classes.Values.Where(c => original.ContainsKey(c.ClassId)))
                            Assert.AreEqual(cls.BaseClassId, saved.Classes[cls.ClassId].BaseClassId, "existing BCMAP parent must not change");
                    }
                }
                finally { if (File.Exists(candidate)) File.Delete(candidate); }
            }
        }

        [TestMethod]
        public void CompositedStartPanelMapsNamesRatherThanNumericIds()
        {
            using (var stock = new VisualStyle())
            using (var source = new VisualStyle())
            using (var target = new VisualStyle())
            {
                stock.Load(Luna); source.Load(Luna); target.Load(@"..\..\..\styles\aero11_7_clear.msstyles");
                var sourceNames = SemanticModel.PartNames("StartPanel", Platform.WinXP);
                foreach (var part in Class(source, "StartPanel").Parts.Values)
                {
                    var size = Property(source, "StartPanel", part.PartId, IDENTIFIER.DEFAULTPANESIZE);
                    if (size != null) size.SetValue(new Margins(-part.PartId, 0, 375 + part.PartId, 90 + part.PartId));
                }
                int counterpartId = target.Classes.Keys.Max() + 1;
                var counterpart = new StyleClass { ClassId = counterpartId, ClassName = "StartPanelComposited::StartPanelPriv" };
                foreach (int partId in new[] { 1, 2, 6, 9, 14 })
                {
                    var part = new StylePart { PartId = partId, PartName = SemanticModel.PartNames(counterpart.ClassName, target.Platform)[partId] };
                    var state = new StyleState { StateId = 0, StateName = "Common Properties" };
                    var pane = new StyleProperty(new PropertyHeader((int)IDENTIFIER.DEFAULTPANESIZE, (int)IDENTIFIER.RECTTYPE)
                        { classID = counterpartId, partID = partId, stateID = 0 });
                    pane.SetValue(new Margins(0, 0, 1, 1));
                    state.Properties.Add(pane); part.States.Add(0, state); counterpart.Parts.Add(partId, part);
                }
                target.Classes.Add(counterpartId, counterpart);
                var importer = Importer(stock, source, target);
                Invoke(importer, "StartPanelCounterpartsFromXp");
                int checkedParts = 0;
                foreach (var cls in target.Classes.Values.Where(c => c.ClassName.StartsWith("StartPanelComposited", StringComparison.OrdinalIgnoreCase)))
                    foreach (var part in cls.Parts.Values)
                    {
                        var names = SemanticModel.PartNames(cls.ClassName, target.Platform);
                        if (!names.TryGetValue(part.PartId, out string name)) continue;
                        if (name == "PLACESLIST") name = "PLACELIST";
                        if (name == "PLACESLISTSEPARATOR") name = "PLACELISTSEPARATOR";
                        if (name == "LOGOFFBACKGROUND") name = "LOGOFF";
                        var match = sourceNames.Where(p => p.Value == name).ToList();
                        if (match.Count != 1) continue;
                        var original = Property(source, "StartPanel", match[0].Key, IDENTIFIER.DEFAULTPANESIZE);
                        if (original == null) continue;
                        Assert.AreEqual(original.GetValue(), Property(target, cls.ClassName, part.PartId, IDENTIFIER.DEFAULTPANESIZE)?.GetValue());
                        checkedParts++;
                    }
                Assert.IsTrue(checkedParts > 0, "fixture must exercise semantic Start counterparts");
            }
        }

        [TestMethod]
        public void ExplicitSourceArtworkPreferenceIsNarrowAndBehavioral()
        {
            using (var stock = new VisualStyle())
            using (var source = new VisualStyle())
            using (var target = new VisualStyle())
            {
                stock.Load(Luna); source.Load(Luna); target.Load(Luna);
                var importer = Importer(stock, source, target);
                foreach (string part in new[] { "NORMALGROUPBACKGROUND", "SPECIALGROUPBACKGROUND" })
                    Assert.IsTrue((bool)Invoke(importer, "PreferSourceRecord", new SemanticEntry { ClassName = "ExplorerBar", PartName = part, NameId = (int)IDENTIFIER.IMAGEFILE, ResourceType = "IMAGE" }));
                Assert.IsTrue((bool)Invoke(importer, "PreferSourceRecord", new SemanticEntry { ClassName = "ExplorerBar::Rebar", PartName = "Common Properties", NameId = (int)IDENTIFIER.IMAGEFILE, ResourceType = "IMAGE" }));
                Assert.IsFalse((bool)Invoke(importer, "PreferSourceRecord", new SemanticEntry { ClassName = "Header", PartName = "HEADERITEM", NameId = (int)IDENTIFIER.SIZINGMARGINS }));
            }
        }

        [TestMethod]
        public void HighDpiSlotsFillFromSourceArtworkNotTemplate()
        {
            using (var stock = new VisualStyle())
            using (var source = new VisualStyle())
            using (var target = new VisualStyle())
            {
                stock.Load(Luna); source.Load(Luna); target.Load(Luna);
                SetStrip(stock, "Button", 1, 5, false, Pattern(5, false, 0xFF000000));
                SetStrip(source, "Button", 1, 5, false, Pattern(5, false, 0xFF100000));
                SetStrip(target, "Button", 1, 5, false, Pattern(5, false, 0xFF001000));
                var cls = Class(target, "Button");
                var slot = new StyleProperty(new PropertyHeader((int)IDENTIFIER.IMAGEFILE5, (int)IDENTIFIER.FILENAME)
                    { classID = cls.ClassId, partID = 1, stateID = 0 });
                slot.SetValue(Property(target, "Button", 1, IDENTIFIER.IMAGEFILE).Header.shortFlag);
                cls.Parts[1].States[0].Properties.Add(slot);
                var importer = Importer(stock, source, target);
                Invoke(importer, "MarkTouched", Property(target, "Button", 1, IDENTIFIER.IMAGEFILE), "probe");
                Invoke(importer, "FillDpiSlots", new Dictionary<string, string>());
                var allocator = (ResourceIdAllocator)typeof(Win7StyleImporter).GetField("m_alloc", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(importer);
                var filled = ImageIdentity.Decode(allocator.Added["IMAGE/" + (slot.Header.shortFlag & 0xFFFF)]);
                Assert.AreEqual(Strip(source, "Button", 1).PixelSha1(), filled.PixelSha1());
            }
        }

        [TestMethod]
        public void FrameSynthesisTilesRepeatsAndRejectsInvalidGeometry()
        {
            var method = typeof(Win7StyleImporter).Assembly.GetType("libmsstyle.Import.DwmImport", true).GetMethod("SynthesizeXpFrameStrip", HiddenStatic);
            var source = new ArgbImage(4, 1);
            source.Pixels[0] = 0xFF110000; source.Pixels[1] = 0xFF220000;
            source.Pixels[2] = 0xFF330000; source.Pixels[3] = 0xFF440000;
            object[] args = { source, 1, false, 10, 7, 2, new Margins(1, 0, 1, 0), true, null };
            var actual = (ArgbImage)method.Invoke(null, args);
            uint[] expected = { 0xFF110000, 0xFF110000, 0xFF220000, 0xFF330000, 0xFF220000, 0xFF330000, 0xFF220000, 0xFF330000, 0xFF440000, 0xFF440000 };
            for (int y = 0; y < 7; ++y)
                for (int x = 0; x < 10; ++x) Assert.AreEqual(expected[x], actual.Pixels[y * 10 + x]);
            object[] bad = { source, 1, false, 2, 7, 2, new Margins(0, 0, 0, 0), false, null };
            Assert.IsNull(method.Invoke(null, bad));
            bad[3] = 10; bad[1] = 3;
            Assert.IsNull(method.Invoke(null, bad));
        }

        [TestMethod]
        public void PackIconWinsAndLegacyDesktopAliasKeepsSameArtwork()
        {
            string dir = Path.Combine(Path.GetTempPath(), "xp-icons-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string theme = Path.Combine(dir, "Probe.theme");
                File.WriteAllBytes(Path.Combine(dir, "probe.ico"), new byte[] { 0, 0, 1, 0, 0, 0 });
                File.WriteAllText(theme, "[CLSID\\{450D8FBA-AD25-11D0-98A8-0800361B1103}\\DefaultIcon]\r\nDefaultValue=probe.ico,0\r\n");
                var extras = XpThemeExtras.Read(new XpThemeFiles.Desktop { ThemePath = theme }, dir, Path.Combine(dir, "output"), "Probe", false);
                Assert.AreEqual(2, extras.Icons.Count);
                Assert.IsTrue(extras.Icons.Any(i => i.Section.Contains("59031A47")));
                Assert.AreEqual(extras.Icons[0].Value, extras.Icons[1].Value);
                StringAssert.Contains(extras.Icons[0].Value, @"\Probe\Icons\probe.ico,0");
                Assert.AreEqual(0, extras.Metrics.Count);
            }
            finally { Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void OriginalXpGroupedIconExtractionPreservesEveryFrame()
        {
            string root = Environment.GetEnvironmentVariable("XP_THEME_SYSTEM_ASSETS");
            if (string.IsNullOrWhiteSpace(root) && Directory.Exists(@"..\..\..\..\work\xp-system-assets"))
                root = Path.GetFullPath(@"..\..\..\..\work\xp-system-assets");
            string binary = root == null ? null : new[] { Path.Combine(root, "shell32.dll"), Path.Combine(root, "System32", "shell32.dll"), Path.Combine(root, "Windows", "System32", "shell32.dll") }.FirstOrDefault(File.Exists);
            if (binary == null) Assert.Inconclusive("provide work/xp-system-assets or set XP_THEME_SYSTEM_ASSETS to original XP system binaries");
            using (var inventory = new ResourceInventory(binary))
            {
                var groups = inventory.OfType("#14").GroupBy(e => e.Name).Select(g => g.First())
                    .OrderBy(e => e.TryGetIntName(out int id) ? id : int.MaxValue).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
                Assert.IsTrue(groups.Count > 0);
                var type = typeof(XpThemeExtras).GetNestedType("XpIconAssets", BindingFlags.NonPublic);
                var instance = Activator.CreateInstance(type, root);
                var method = type.GetMethod("Extract");
                var ico = (byte[])method.Invoke(instance, new object[] { binary, 0 });
                var group = inventory.GetData(groups[0]);
                int count = BitConverter.ToUInt16(group, 4);
                Assert.AreEqual(count, (int)BitConverter.ToUInt16(ico, 4));
                for (int frame = 0; frame < count; ++frame)
                {
                    int entry = 6 + frame * 16;
                    int length = BitConverter.ToInt32(ico, entry + 8), offset = BitConverter.ToInt32(ico, entry + 12);
                    string id = "#" + BitConverter.ToUInt16(group, 6 + frame * 14 + 12);
                    var resource = inventory.OfType("#3").FirstOrDefault(e => e.Name == id && e.Language == groups[0].Language)
                        ?? inventory.OfType("#3").First(e => e.Name == id);
                    CollectionAssert.AreEqual(inventory.GetData(resource), ico.Skip(offset).Take(length).ToArray());
                }
                if (groups[0].TryGetIntName(out int resourceId))
                    CollectionAssert.AreEqual(ico, (byte[])method.Invoke(instance, new object[] { binary, -resourceId }));
            }
        }

        [TestMethod]
        public void CaptionButtonFramesUseExplicitActiveInactiveStateOrder()
        {
            var type = typeof(Win7StyleImporter).Assembly.GetType("libmsstyle.Import.DwmImport", true);
            var method = type.GetMethod("CutXpButtonFrame", HiddenStatic);
            foreach (bool horizontal in new[] { false, true })
                foreach (int count in new[] { 1, 4, 8 })
                    for (int state = 0; state < 4; ++state)
                        foreach (bool inactive in new[] { false, true })
                        {
                            var actual = (ArgbImage)method.Invoke(null, new object[] { Pattern(count, horizontal, 0xFF100000), count, horizontal, state, inactive });
                            int frame = count == 1 ? 0 : state + (count == 8 && inactive ? 4 : 0);
                            Assert.AreEqual(3, actual.Width); Assert.AreEqual(3, actual.Height);
                            Assert.AreEqual(0xFF100000u + (uint)(frame * 0x080808), actual.Pixels[0]);
                        }
            Assert.IsNull(method.Invoke(null, new object[] { Pattern(6, true, 0xFF100000), 6, true, 0, false }));
        }

        [TestMethod]
        public void CaptionGlyphPaddingCentersWithoutUpscalingAndShrinksProportionally()
        {
            var method = typeof(Win7StyleImporter).Assembly.GetType("libmsstyle.Import.DwmImport", true).GetMethod("PadXpGlyph", HiddenStatic);
            var glyph = Pattern(1, false, 0xFF100000);
            var padded = (ArgbImage)method.Invoke(null, new object[] { glyph, 9, 9 });
            Assert.AreEqual(9, padded.Pixels.Count(p => (p >> 24) != 0), "glyph must remain 3x3, not upscale");
            for (int y = 0; y < 3; ++y)
                for (int x = 0; x < 3; ++x) Assert.AreEqual(glyph.Pixels[y * 3 + x], padded.Pixels[(y + 3) * 9 + x + 3]);
            Assert.AreEqual(0u, padded.Pixels[0], "unused transparent padding must stay transparent");
            var wide = new ArgbImage(8, 4);
            for (int i = 0; i < wide.Pixels.Length; ++i) wide.Pixels[i] = 0xFF123456;
            var shrunk = (ArgbImage)method.Invoke(null, new object[] { wide, 6, 6 });
            Assert.AreEqual(12, shrunk.Pixels.Count(p => (p >> 24) != 0), "4x2 native-aspect glyph plus duplicated left/right inset pixels");
            Assert.IsNull(method.Invoke(null, new object[] { glyph, 2, 9 }));
        }

        [TestMethod]
        public void DuplicateClassEntriesRemainVisibleWithoutChangingTemplateValues()
        {
            using (var style = new VisualStyle())
            {
                style.Load(@"..\..\..\styles\aero11_7_clear.msstyles");
                int originalId = style.Classes.Keys.Max() + 1;
                for (int copy = 0; copy < 2; ++copy)
                {
                    int id = originalId + copy;
                    var duplicate = new StyleClass { ClassId = id, ClassName = "TaskbandExtendedUI" };
                    var part = new StylePart { PartId = 0, PartName = "Common Properties" };
                    var state = new StyleState { StateId = 0, StateName = "Common Properties" };
                    var property = new StyleProperty(new PropertyHeader((int)IDENTIFIER.FILLCOLOR, (int)IDENTIFIER.COLOR)
                        { classID = id, partID = 0, stateID = 0 });
                    property.SetValue(System.Drawing.Color.FromArgb(10 + copy, 20, 30));
                    state.Properties.Add(property); part.States.Add(0, state); duplicate.Parts.Add(0, part);
                    style.Classes.Add(id, duplicate);
                }
                var cls = style.Classes.Values.GroupBy(c => c.ClassName, StringComparer.OrdinalIgnoreCase)
                    .Single(g => g.Key.Equals("TaskbandExtendedUI", StringComparison.OrdinalIgnoreCase));
                var before = cls.SelectMany(c => c.Parts.Values).SelectMany(p => p.States.Values).SelectMany(s => s.Properties)
                    .ToDictionary(p => p, SemanticModel.FormatValue);
                var model = new SemanticModel(style);
                Assert.AreEqual(before.Count, model.Entries.Count(e => e.ClassName.Equals(cls.Key, StringComparison.OrdinalIgnoreCase)));
                foreach (var record in before) Assert.AreEqual(record.Value, SemanticModel.FormatValue(record.Key));
            }
        }

        [TestMethod]
        public void SizeVariantRemapsIndependentClassIdsBySemanticName()
        {
            string main = Environment.GetEnvironmentVariable("MSSTYLE_VARIANT_MAIN");
            string size = Environment.GetEnvironmentVariable("MSSTYLE_VARIANT_SIZE");
            if (!File.Exists(main) || !File.Exists(size))
                Assert.Inconclusive("set MSSTYLE_VARIANT_MAIN and MSSTYLE_VARIANT_SIZE to independently generated size ports with differing class IDs");
            string output = Path.Combine(Path.GetTempPath(), "xp-size-map-" + Guid.NewGuid().ToString("N") + ".msstyles");
            try
            {
                using (var normal = new VisualStyle())
                using (var large = new VisualStyle())
                {
                    normal.Load(main); large.Load(size);
                    var normalClasses = normal.Classes.Values.GroupBy(c => c.ClassName, StringComparer.OrdinalIgnoreCase)
                        .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);
                    var changed = large.Classes.Values.FirstOrDefault(c => normalClasses.ContainsKey(c.ClassName)
                        && normalClasses[c.ClassName].ClassId != c.ClassId && c.Parts.Values.Any(p => p.States.Values.Any(s => s.Properties.Count > 0)));
                    if (changed == null) Assert.Inconclusive("size-port fixtures must differ in a populated class ID");
                    int expected = normalClasses[changed.ClassName].ClassId;
                    File.Copy(main, output);
                    VariantWriter.Apply(output, "Size map regression", "NormalColor", "Normal", new[]
                    { new VariantWriter.Size { Name = "LargeFonts", DisplayName = "Large", PortPath = size } });
                    using (var inv = new ResourceInventory(output))
                    {
                        byte[] data = inv.GetData(inv.Entries.Single(e => e.Type == "VARIANT" && e.Name == "S_LARGEFONTS"));
                        var reader = typeof(VisualStyle).Assembly.GetType("libmsstyle.PropertyStream", true).GetMethod("ReadNextProperty");
                        var records = new List<StyleProperty>();
                        int offset = 0;
                        while (offset + 32 <= data.Length)
                        {
                            object[] args = { data, offset };
                            records.Add((StyleProperty)reader.Invoke(null, args));
                            offset = (int)args[1];
                        }
                        int sourceCount = changed.Parts.Values.Sum(p => p.States.Values.Sum(s => s.Properties.Count));
                        Assert.AreEqual(sourceCount, records.Count(p => p.Header.classID == expected), changed.ClassName);
                        var expectedKeys = changed.Parts.Values.SelectMany(p => p.States.Values).SelectMany(s => s.Properties)
                            .Select(p => p.Header.partID + "/" + p.Header.stateID + "/" + p.Header.nameID + "/" + p.Header.typeID).ToArray();
                        CollectionAssert.AreEquivalent(expectedKeys, records.Where(p => p.Header.classID == expected)
                            .Select(p => p.Header.partID + "/" + p.Header.stateID + "/" + p.Header.nameID + "/" + p.Header.typeID).ToArray());
                    }
                }
            }
            finally { if (File.Exists(output)) File.Delete(output); }
        }

        [TestMethod]
        public void ExplicitExplorerBarHeaderMarginsWinShellstyleInsets()
        {
            using (var stock = new VisualStyle())
            using (var source = new VisualStyle())
            using (var target = new VisualStyle())
            using (var shell = new XpShellStyle(StockStyles.XpLunaShellPath("NormalColor")))
            {
                stock.Load(Luna); source.Load(Luna); target.Load(Luna);
                foreach (int part in new[] { 8, 12 })
                {
                    var margin = Property(source, "ExplorerBar", part, IDENTIFIER.CONTENTMARGINS);
                    if (margin == null)
                    {
                        margin = new StyleProperty(new PropertyHeader((int)IDENTIFIER.CONTENTMARGINS, (int)IDENTIFIER.MARGINS)
                            { classID = Class(source, "ExplorerBar").ClassId, partID = part, stateID = 0 });
                        Class(source, "ExplorerBar").Parts[part].States[0].Properties.Add(margin);
                    }
                    margin.SetValue(new Margins(part, 3, 17, 5));
                }
                var importer = Importer(stock, source, target);
                var type = typeof(Win7StyleImporter).Assembly.GetType("libmsstyle.Import.XpShellImport", true);
                var pane = Activator.CreateInstance(type, importer, shell, shell, false);
                type.GetMethod("Snapshot").Invoke(pane, null);
                foreach (int part in new[] { 8, 12 })
                {
                    type.GetMethod("HeaderInset", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(pane, new object[] { part, part == 12, "probe" });
                    Assert.AreEqual(new Margins(part, 3, 17, 5), Property(target, "ExplorerBar", part, IDENTIFIER.CONTENTMARGINS).GetValue());
                }
            }
        }

    }
}
