using libmsstyle;
using libmsstyle.Import;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace libmsstyleTests
{
    [TestClass]
    public class Win7ImportTests
    {
        const string W7Aero = @"..\..\..\styles\w7_aero.msstyles";
        const string Aero11 = @"..\..\..\styles\aero11_7_clear.msstyles";

        // Shine 2.0 isn't redistributable, so it isn't in the repo. Point MSSTYLE_SHINE at it to run those tests.
        static string ShinePath =>
            Environment.GetEnvironmentVariable("MSSTYLE_SHINE") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                @"Downloads\shine_2_0_for_windows_7_by_zainadeel_d2usxxy\THEME SHINE 2.0\Bottom\Win7-Shine 2.0\Win7-Shine 2.0.msstyles");

        string m_dir;

        [TestInitialize]
        public void Init()
        {
            m_dir = Path.Combine(Path.GetTempPath(), "msstyle-import-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(m_dir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(m_dir, true); } catch (Exception) { }
        }

        static byte[] Variant(string path)
        {
            using (var inv = new ResourceInventory(path))
                return inv.GetData(inv.Entries.Single(e => e.Type == "VARIANT"));
        }

        [TestMethod]
        public void TemplateSavedWithoutChangesIsByteIdentical()
        {
            string output = Path.Combine(m_dir, "noop.msstyles");
            using (var s = new VisualStyle())
            {
                s.Load(Aero11);
                Assert.AreEqual(0, s.LoadDiagnostics.Count, string.Join("\n", s.LoadDiagnostics));
                s.Save(output);
            }
            var v = StyleValidator.ValidateUnchanged(Aero11, output);
            // no run blocked this report, so a clean validation passes; a blocked one never would, see
            // OutputNextToAnInputIsRefused.
            Assert.IsTrue(v.Passed, string.Join("\n", v.Errors));
            CollectionAssert.AreEqual(Variant(Aero11), Variant(output));
        }

        [TestMethod]
        public void ImportingStockWin7OntoTemplateChangesNothing()
        {
            // no BasePath: the built-in stock aero has to be the same file as the repo copy.
            CollectionAssert.AreEqual(File.ReadAllBytes(W7Aero), File.ReadAllBytes(StockStyles.Win7AeroPath()));

            string output = Path.Combine(m_dir, "self.msstyles");
            var r = Win7StyleImporter.Run(new Win7ImportOptions
            {
                SourcePath = W7Aero,
                TemplatePath = Aero11,
                OutputPath = output,
                DryRun = false
            });
            Assert.AreEqual(ImportStatus.ReadyForVmTest, r.Status, string.Join("\n", r.Errors));
            Assert.IsFalse(r.Counts.ContainsKey("applied"));
            Assert.AreEqual(0, r.Resources.Count);
            Assert.AreEqual(r.Dwm.TemplateAtlasId, r.Dwm.NewAtlasId);
            Assert.AreEqual(0, r.Dwm.FallbackParts.Count);
            // the only addition: win7's highlight parts, kept under DWMWindow 95/96 for DWM mods.
            Assert.AreEqual(2, r.Counts["dwm-highlight-kept"]);
            using (var t = new VisualStyle())
            using (var o = new VisualStyle())
            {
                t.Load(Aero11);
                o.Load(output);
                var tm = new SemanticModel(t);
                var om = new SemanticModel(o);
                bool Highlight(SemanticEntry e) => e.ClassName == "DWMWindow" && (e.PartId == 95 || e.PartId == 96);
                Assert.AreEqual(0, tm.Entries.Count(Highlight));
                var rest = om.Entries.Where(e => !Highlight(e)).ToList();
                Assert.AreEqual(tm.Entries.Count, rest.Count);
                foreach (var e in rest)
                    Assert.AreEqual(tm.ByKey[e.Key].RawValue, e.RawValue, e.Key);
            }
        }

        [TestMethod]
        public void Win7HighlightPartsAreKeptUnderFreeDwmIds()
        {
            // win7's LEFTHIGHLIGHTCORNER (34) and SIDEHIGHLIGHT (43) have no win10 consumer and their ids
            // belong to other parts there. they're kept as 95/96, with the rect, frame count, margins and
            // per-state opacities OpenGlass reads.
            string output = Path.Combine(m_dir, "highlights.msstyles");
            var r = Win7StyleImporter.Run(new Win7ImportOptions { SourcePath = W7Aero, TemplatePath = Aero11, OutputPath = output, DryRun = false });
            Assert.AreEqual(ImportStatus.ReadyForVmTest, r.Status, string.Join("\n", r.Errors));
            using (var w7 = new VisualStyle())
            using (var o = new VisualStyle())
            {
                w7.Load(W7Aero);
                o.Load(output);
                var wm = new SemanticModel(w7);
                var om = new SemanticModel(o);
                foreach (var (name, id) in new[] { ("LEFTHIGHLIGHTCORNER", 95), ("SIDEHIGHLIGHT", 96) })
                {
                    var src = wm.Entries.Where(e => e.ClassName == "DWMWindow" && e.PartName == name).ToList();
                    var kept = om.Entries.Where(e => e.ClassName == "DWMWindow" && e.PartId == id).ToList();
                    Assert.IsTrue(src.Count > 0, name);
                    Assert.AreEqual(src.Count, kept.Count, name);
                    foreach (var se in src)
                        Assert.AreEqual(se.RawValue, kept.Single(k => k.StateId == se.StateId && k.NameId == se.NameId).RawValue, $"{name} S{se.StateId} {se.PropertyName}");
                }
            }
        }

        // The win7 and win10 DWMWindow part tables number parts differently. Aero11 kept the stock
        // win7 atlas, so a correct pair of tables puts the same part name on the same stock rect.
        [TestMethod]
        public void Win10DwmPartNamesPointAtTheSameStockSpritesAsWin7()
        {
            using (var w7 = new VisualStyle())
            using (var a11 = new VisualStyle())
            {
                w7.Load(W7Aero);
                a11.Load(Aero11);
                var wm = new SemanticModel(w7);
                var am = new SemanticModel(a11);
                Func<SemanticModel, Dictionary<string, Rectangle>> rects = m => m.Entries
                    .Where(e => e.ClassName == "DWMWindow" && e.PropertyName == "ATLASRECT" && e.StateId == 0)
                    .ToDictionary(e => e.PartName, e => DwmImport.RectOf(e));
                var wr = rects(wm);
                var ar = rects(am);

                var shared = ar.Keys.Where(wr.ContainsKey).ToList();
                var mismatched = shared.Where(n => wr[n] != ar[n]).ToList();
                Assert.IsTrue(shared.Count >= 60, $"only {shared.Count} part names shared");
                // aero11 points the 192dpi small close glyph at the restore glyph; everything else lines up.
                CollectionAssert.AreEquivalent(new[] { "SMALLBUTTONCLOSEGLYPH192" }, mismatched);
                // win7 part 12 and win10 part 11 are the same sprite.
                Assert.AreEqual(12, wm.Entries.First(e => e.ClassName == "DWMWindow" && e.PartName == "BUTTONCLOSEGLYPH96").PartId);
                Assert.AreEqual(11, am.Entries.First(e => e.ClassName == "DWMWindow" && e.PartName == "BUTTONCLOSEGLYPH96").PartId);
            }
        }

        [TestMethod]
        public void ShineImportMapsEveryDwmSpriteAndPassesValidation()
        {
            if (!File.Exists(ShinePath)) Assert.Inconclusive("Shine 2.0 not found at " + ShinePath);
            string output = Path.Combine(m_dir, "shine.msstyles");
            var r = Win7StyleImporter.Run(new Win7ImportOptions
            {
                SourcePath = ShinePath,
                TemplatePath = Aero11,
                OutputPath = output,
                DryRun = false
            });
            Assert.AreEqual(ImportStatus.ReadyForVmTest, r.Status, string.Join("\n", r.Errors));
            Assert.IsTrue(File.Exists(output));
            Assert.IsTrue(r.Dwm.TemplateAtlasIsStockWin7);
            Assert.AreEqual(0, r.Dwm.FallbackParts.Count);
            Assert.IsFalse(r.Dwm.AtlasReencoded, "no kept sprites, so the source atlas bytes must go in untouched");

            var byPart = r.Dwm.Parts.ToDictionary(p => p.TargetPartId);
            foreach (int id in new[] { 1, 3, 4, 7, 8, 11, 12, 13, 14, 31, 33, 46 })
                Assert.AreEqual("name", byPart[id].Method, byPart[id].TargetPart);
            // dark glyphs and dark caption backgrounds follow their light counterpart.
            Assert.AreEqual(byPart[11].NewRect, byPart[64].NewRect);
            Assert.AreEqual(byPart[3].NewRect, byPart[88].NewRect);

            using (var shine = new VisualStyle())
            using (var outStyle = new VisualStyle())
            using (var tmplInv = new ResourceInventory(Aero11))
            {
                shine.Load(ShinePath);
                outStyle.Load(output);
                var sm = new SemanticModel(shine);
                var om = new SemanticModel(outStyle);
                var atlas = om.Entries.Single(e => e.ClassName == "DWMWindow" && e.PartId == 0 && e.TypeId == (int)IDENTIFIER.DISKSTREAM);
                Assert.IsFalse(tmplInv.Entries.Any(e => e.Type == "STREAM" && e.Name == "#" + atlas.ResourceId), "atlas id collides with the template");

                var sAtlas = ImageIdentity.Decode(sm.GetResourceBytes("STREAM", sm.Entries.Single(e => e.ClassName == "DWMWindow" && e.PartId == 0 && e.TypeId == (int)IDENTIFIER.DISKSTREAM).ResourceId));
                var oAtlas = ImageIdentity.Decode(om.GetResourceBytes("STREAM", atlas.ResourceId));
                var sClose = DwmImport.RectOf(sm.Entries.Single(e => e.ClassName == "DWMWindow" && e.PartName == "BUTTONCLOSEGLYPH96" && e.PropertyName == "ATLASRECT"));
                var oClose = DwmImport.RectOf(om.Entries.Single(e => e.ClassName == "DWMWindow" && e.PartId == 11 && e.PropertyName == "ATLASRECT"));
                Assert.IsTrue(Win7StyleImporter.NearlyEqual(sAtlas.Crop(sClose), oAtlas.Crop(oClose), 0));

                var shadow = om.Entries.Single(e => e.ClassName == "DWMWindow" && e.PartName == "LEFTSHADOW" && e.PropertyName == "SIZINGMARGINS");
                var m = (Margins)shadow.Property.GetValue();
                Assert.AreEqual(118, m.Top);
                Assert.AreEqual(118, m.Bottom);

                // shine keeps RESTOREBUTTON stock; aero11 only shares its image id with MAXBUTTON.
                Assert.IsFalse(r.Decisions.Any(d => d.Class == "Window" && d.Part == "RESTOREBUTTON" && d.Action.StartsWith("applied")));

                // aero11's selected tab shows the stock left-edge strip, so it has to get shine's left edge.
                var tab = r.Decisions.Single(d => d.Class == "Tab" && d.Part == "TOPTABITEM" && d.Property == "IMAGEFILE");
                Assert.AreEqual("applied-alias", tab.Action, tab.Reason);
                var oTab = om.Entries.Single(e => e.ClassName == "Tab" && e.PartName == "TOPTABITEM" && e.PropertyName == "IMAGEFILE");
                var sEdge = sm.Entries.Single(e => e.ClassName == "Tab" && e.PartName == "TABITEMLEFTEDGE" && e.PropertyName == "IMAGEFILE");
                Assert.AreEqual(sEdge.Value, oTab.Value);
            }
        }

        [TestMethod]
        public void FontStringsAreReadTheWayUxthemeReadsThem()
        {
            // same font, different spelling.
            Assert.AreEqual(FontSpec.Parse("Segoe UI, 8, Quality:Cleartype"), FontSpec.Parse("segoe ui,8,quality:ClearType"));
            Assert.AreNotEqual(FontSpec.Parse("Segoe UI, 8"), FontSpec.Parse("Segoe UI, 9"));
            Assert.AreEqual(12, FontSpec.Parse("Segoe UI, 9").PixelHeight96);
            Assert.AreEqual(16, FontSpec.Parse("Tahoma, 0xC").PixelHeight96);

            // uxtheme fails the whole theme load on an unknown style word.
            Assert.IsNotNull(FontSpec.Parse("Segoe UI, 9, Heavy").Error);
            Assert.IsTrue(FontSpec.Parse("Segoe UI, 9, bold, Quality:ClearType-Natural").Bold);

            // "Face,9 Bold": the size token runs to the next comma, Bold is never seen.
            var noComma = FontSpec.Parse("Segoe UI, 9 Bold");
            Assert.IsFalse(noComma.Bold);
            Assert.IsTrue(noComma.Warnings.Count > 0);

            Assert.IsFalse(FontSpec.Parse("Segoe UI, 0").IsSane);
            Assert.IsFalse(FontSpec.Parse("Segoe UI, Bold").IsSane, "no size: uxtheme stops parsing and uses height 0");
        }

        [TestMethod]
        public void DreamFontsAndFramesComeThroughWhole()
        {
            // Dream (deviantart, not redistributable): fonts and text glow on records stock aero doesn't
            // have, a font string the template lacks, and frame sprites with odd geometry it shipped like that on win7.
            string dream = Environment.GetEnvironmentVariable("MSSTYLE_DREAM") ?? @"F:\abaaaa\Dream\Dream.msstyles";
            if (!File.Exists(dream)) Assert.Inconclusive("Dream not found at " + dream);
            string output = Path.Combine(m_dir, "dream.msstyles");
            var r = Win7StyleImporter.Run(new Win7ImportOptions { SourcePath = dream, TemplatePath = Aero11, OutputPath = output, DryRun = false });
            Assert.AreEqual(ImportStatus.ReadyForVmTest, r.Status, string.Join("\n", r.Errors));

            Assert.IsTrue(r.Counts["added"] > 0);
            Assert.IsTrue(r.Counts["font-strings-added"] > 0);
            Assert.AreEqual(0, r.Dwm.FallbackParts.Count, "dream's own top/left frames must not be swapped for aero11's");

            using (var outStyle = new VisualStyle())
            {
                outStyle.Load(output);
                var om = new SemanticModel(outStyle);
                var tree = om.Entries.First(e => e.ClassName == "Explorer::TreeView" && e.PartName == "TREEITEM" && e.TypeId == (int)IDENTIFIER.FONT);
                var font = FontSpec.Parse(om.ResolveString(tree.Property.Header.shortFlag));
                Assert.IsTrue(font.IsSane, "added font record must point at a string uxtheme can parse");

                // address bar text glow: stock win7 has no TEXTGLOWSIZE on BBComposited::Toolbar, dream sets 5.
                var glow = om.Entries.SingleOrDefault(e => e.ClassName == "BBComposited::Toolbar" && e.StateId == 0 && e.NameId == (int)IDENTIFIER.TEXTGLOWSIZE);
                Assert.IsNotNull(glow, "source-added text glow missing from the output");
                Assert.AreEqual("5", glow.Value);

                // dream's win7 nav pane is ProperTree's FILLCOLORHINT (238), not its tree fill (219). win10 reads
                // ExplorerNavPane for the pane and ProperTree FILLCOLOR for the host's 1px left padding.
                Assert.AreEqual("rgb(238,238,238)", om.Entries.Single(e => e.ClassName == "ExplorerNavPane" && e.PartId == 0 && e.StateId == 0 && e.NameId == (int)IDENTIFIER.FILLCOLOR).Value);
                Assert.AreEqual("rgb(238,238,238)", om.Entries.Single(e => e.ClassName == "ProperTree" && e.PartId == 2 && e.StateId == 0 && e.NameId == (int)IDENTIFIER.FILLCOLOR).Value);

                // unique-color probes identify ItemsView common text on win10; win7 uses WINDOWTEXT.
                var commonText = SemanticModel.FindEffective(outStyle, om.ClassesByName["ItemsView"], 0, 0, IDENTIFIER.TEXTCOLOR);
                Assert.AreEqual("rgb(92,92,92)", SemanticModel.FormatValue(commonText), "drive tile title");
                var navText = SemanticModel.FindEffective(outStyle, om.ClassesByName["ExplorerNavPane"], 0, 0, IDENTIFIER.TEXTCOLOR);
                Assert.AreEqual("rgb(92,92,92)", SemanticModel.FormatValue(navText), "normal nav labels");
                foreach (int state in new[] { 2, 3 })
                {
                    var filename = SemanticModel.FindEffective(outStyle, om.ClassesByName["ItemsView"], 4, state, IDENTIFIER.TEXTCOLOR);
                    Assert.AreEqual("rgb(92,92,92)", SemanticModel.FormatValue(filename), "file name, state " + state);
                }
                var subtitle = SemanticModel.FindEffective(outStyle, om.ClassesByName["ItemsView"], 4, 1, IDENTIFIER.TEXTCOLOR);
                Assert.AreEqual("rgb(109,109,109)", SemanticModel.FormatValue(subtitle), "the matching free-space subtitle color must stay unchanged");
            }
        }

        [TestMethod]
        public void EmptyingAClassIsRejected()
        {
            // uxtheme only registers classes that have records, and BCMAP parents index that order.
            // emptying aero11's Explorer::CommandModule (3 colors) shifted Flyout off FlyoutStyle and the
            // tray flyout fell back to the System font, so even declared removals can't empty a class.
            string candidate = Path.Combine(m_dir, "emptied.msstyles");
            var removed = new List<string>();
            using (var style = new VisualStyle())
            {
                style.Load(Aero11);
                var cls = style.Classes.Values.Single(c => c.ClassName == "Explorer::CommandModule");
                foreach (var part in cls.Parts.Values)
                    foreach (var st in part.States.Values)
                    {
                        foreach (var p in st.Properties)
                            removed.Add($"{cls.ClassId}/{part.PartId}/{st.StateId}/{p.Header.nameID}/{p.Header.typeID}");
                        st.Properties.Clear();
                    }
                style.Save(candidate, false);
            }
            Assert.IsTrue(removed.Count > 0);
            var v = new StyleValidator(Aero11, candidate, null, null, null, removedRecords: removed).Validate();
            Assert.IsTrue(v.Errors.Any(e => e.Contains("lost all their records") && e.Contains("Explorer::CommandModule")), string.Join("\n", v.Errors));
        }

        [TestMethod]
        public void PachenkoFramesAndColorsComeThrough()
        {
            // Pachenko (deviantart, not redistributable, x86) ships a 9x121 trackbar thumb with
            // IMAGECOUNT 5 and a 36x55 menu bar in 2; win7 drew them, so they're warnings, not errors.
            // it also darkens ItemsView where aero11 nudged stock 252 to 255, and deletes TaskDialog's
            // panel fills: colors follow the source either way.
            string pachenko = Environment.GetEnvironmentVariable("MSSTYLE_PACHENKO")
                ?? @"C:\Users\arceus\Downloads\Pachenko theme by VIN STAR (Scanned)\Pachenko theme\theme\Aero\Pachenko\Pachenko.msstyles";
            if (!File.Exists(pachenko)) Assert.Inconclusive("Pachenko not found at " + pachenko);
            string output = Path.Combine(m_dir, "pachenko.msstyles");
            var r = Win7StyleImporter.Run(new Win7ImportOptions { SourcePath = pachenko, TemplatePath = Aero11, OutputPath = output, DryRun = false });
            Assert.AreEqual(ImportStatus.ReadyForVmTest, r.Status, string.Join("\n", r.Errors));
            Assert.IsTrue(r.Warnings.Any(w => w.Contains("TrackBar|THUMBTOP|S0|IMAGEFILE2") && w.Contains("same as the source ships it")));

            using (var outStyle = new VisualStyle())
            {
                outStyle.Load(output);
                var om = new SemanticModel(outStyle);
                var fill = om.Entries.Single(e => e.ClassName == "ItemsView" && e.PartId == 0 && e.StateId == 0 && e.NameId == (int)IDENTIFIER.FILLCOLOR);
                Assert.AreEqual("rgb(26,26,26)", fill.Value);
                Assert.IsFalse(om.Entries.Any(e => e.ClassName == "TaskDialog" && e.PartName == "PRIMARYPANEL" && e.NameId == (int)IDENTIFIER.FILLCOLOR),
                    "the source deleted this color, the output must too");

                // explorer's nav pane draws from the win10-only ExplorerNavPane; pachenko darkened Explorer::TreeView.
                var pane = om.Entries.Single(e => e.ClassName == "ExplorerNavPane" && e.PartId == 0 && e.StateId == 0 && e.NameId == (int)IDENTIFIER.FILLCOLOR);
                Assert.AreEqual("rgb(26,26,26)", pane.Value);

                // win10's shellstyle reads the nav pane from PROPERTREE 2/0 FILLCOLOR and the reading pane
                // from ReadingPane 1/0 FILLCOLOR; win7's read FILLCOLORHINT and a GRADIENTCOLOR1-3 gradient.
                Assert.AreEqual("rgb(26,26,26)", om.Entries.Single(e => e.ClassName == "ProperTree" && e.PartId == 2 && e.StateId == 0 && e.NameId == (int)IDENTIFIER.FILLCOLOR).Value);
                Assert.AreEqual("rgb(26,26,26)", om.Entries.Single(e => e.ClassName == "ReadingPane" && e.PartId == 1 && e.StateId == 0 && e.NameId == (int)IDENTIFIER.FILLCOLOR).Value);

                // win7 uses WINDOWTEXT (144), not the source's ItemsView common text (200).
                // the role remap must replace that already-copied source color.
                var titleText = SemanticModel.FindEffective(outStyle, om.ClassesByName["ItemsView"], 0, 0, IDENTIFIER.TEXTCOLOR);
                Assert.AreEqual("rgb(144,144,144)", SemanticModel.FormatValue(titleText), "drive tile title");
                foreach (int state in new[] { 2, 3 })
                {
                    var filename = SemanticModel.FindEffective(outStyle, om.ClassesByName["ItemsView"], 4, state, IDENTIFIER.TEXTCOLOR);
                    Assert.AreEqual("rgb(144,144,144)", SemanticModel.FormatValue(filename), "file name, state " + state);
                }

                // Explorer::Button is new on win10 and adds its own PUSHBUTTON text on top of Button.
                // those records stay and get the source's values copied in; states the source sets
                // apart from state 0 are added, since win10 falls back to the class's own state 0.
                Assert.IsTrue(om.Entries.Any(e => e.ClassName == "Explorer::Button" && e.PartId == 1 && e.StateId == 0 && e.NameId == (int)IDENTIFIER.TEXTCOLOR), "template record must not be deleted");
                Assert.IsTrue(om.Entries.Any(e => e.ClassName == "Explorer::Button" && e.PartId == 1 && e.StateId == 2 && e.NameId == (int)IDENTIFIER.TEXTCOLOR), "source hot text must be added to the win10 class");
                foreach (var expected in new[] { (1, 144), (2, 200), (3, 144), (4, 54), (5, 144), (6, 144) })
                {
                    var text = SemanticModel.FindEffective(outStyle, om.ClassesByName["Explorer::Button"], 1, expected.Item1, IDENTIFIER.TEXTCOLOR);
                    Assert.AreEqual($"rgb({expected.Item2},{expected.Item2},{expected.Item2})", SemanticModel.FormatValue(text), "push button text, state " + expected.Item1);
                }
                var darkText = SemanticModel.FindEffective(outStyle, om.ClassesByName["DarkMode_Explorer::Button"], 1, 1, IDENTIFIER.TEXTCOLOR);
                Assert.AreEqual("rgb(255,255,255)", SemanticModel.FormatValue(darkText), "dark-mode text must still fit the template's dark backgrounds");

                // DrawSizeBoxEx fills from the win10-only background part, not the grip image's hint.
                var sizebox = om.Entries.Single(e => e.ClassName == "ScrollBar" && e.PartId == 11 && e.StateId == 0 && e.NameId == (int)IDENTIFIER.FILLCOLOR);
                Assert.AreEqual("rgb(26,26,26)", sizebox.Value);

                // spin arrows: the source changes their margins and replaces pictures the template had
                // redrawn. the replaced pictures go with the source's geometry instead of locking the part.
                using (var src = new VisualStyle())
                {
                    src.Load(pachenko);
                    var sm = new SemanticModel(src);
                    foreach (var part in new[] { "UP", "DOWN" })
                    {
                        Assert.AreEqual("(l3,t3,r3,b3)", om.Entries.Single(e => e.ClassName == "Spin" && e.PartName == part && e.StateId == 0 && e.NameId == (int)IDENTIFIER.SIZINGMARGINS).Value, part);
                        foreach (var image in new[] { IDENTIFIER.IMAGEFILE, IDENTIFIER.IMAGEFILE1 })
                            Assert.AreEqual(sm.Entries.Single(e => e.ClassName == "Spin" && e.PartName == part && e.StateId == 0 && e.NameId == (int)image).Value,
                                om.Entries.Single(e => e.ClassName == "Spin" && e.PartName == part && e.StateId == 0 && e.NameId == (int)image).Value, part + " " + image);
                    }
                }

                // win10's separate explorer status bar must not keep aero11's white fill and blue text.
                var statusFill = om.Entries.Single(e => e.ClassName == "ExplorerStatusBar" && e.PartId == 0 && e.StateId == 0 && e.NameId == (int)IDENTIFIER.FILLCOLOR);
                Assert.AreEqual("rgb(26,26,26)", statusFill.Value);
                var statusText = om.Entries.Single(e => e.ClassName == "ExplorerStatusBar" && e.PartId == 0 && e.StateId == 0 && e.NameId == (int)IDENTIFIER.TEXTCOLOR);
                Assert.AreEqual("rgb(144,144,144)", statusText.Value);

                // the drive-properties static-label miss isn't explained by missing theme colors.
                foreach (var prop in new[] { IDENTIFIER.WINDOWTEXT, IDENTIFIER.BTNTEXT })
                    Assert.AreEqual("rgb(144,144,144)", om.Entries.Single(e => e.ClassName == "sysmetrics" && e.NameId == (int)prop).Value);
                var tabText = SemanticModel.FindEffective(outStyle, om.ClassesByName["Tab"], 10, 0, IDENTIFIER.TEXTCOLOR);
                Assert.AreEqual("rgb(144,144,144)", SemanticModel.FormatValue(tabText));

                // pachenko changes the scrollbar arrows' margins and has no IMAGEFILE4-6: those slots get
                // its largest arrow strip instead of locking the part to aero11's light arrows.
                var arrows = om.Entries.Where(e => e.ClassName == "ScrollBar" && e.PartName == "ARROWBTN" && e.StateId == 0).ToList();
                var slot3 = arrows.Single(e => e.NameId == (int)IDENTIFIER.IMAGEFILE3).Value;
                Assert.AreEqual(slot3, arrows.Single(e => e.NameId == (int)IDENTIFIER.IMAGEFILE6).Value);
                Assert.AreEqual("(l4,t4,r4,b4)", arrows.Single(e => e.NameId == (int)IDENTIFIER.SIZINGMARGINS).Value);
            }
        }

        [TestMethod]
        public void CandidateGetsTheTemplatesShellAndLanguageFolders()
        {
            // the 2004 aero sample keeps its strings in de-DE/en-GB/ja-JP .mui files and has a Shell folder.
            const string template = @"..\..\..\styles\w10_2004_aero\aero.msstyles";
            string output = Path.Combine(m_dir, "with-shell.msstyles");
            var r = Win7StyleImporter.Run(new Win7ImportOptions
            {
                SourcePath = W7Aero,
                TemplatePath = template,
                OutputPath = output,
                DryRun = false
            });
            Assert.AreEqual(ImportStatus.ReadyForVmTest, r.Status, string.Join("\n", r.Errors));
            Assert.IsTrue(File.Exists(Path.Combine(m_dir, @"Shell\NormalColor\shellstyle.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(m_dir, @"de-DE\with-shell.msstyles.mui")), "the .mui has to be renamed after the new style");
            Assert.IsFalse(Directory.GetDirectories(m_dir).Any(d => Path.GetFileName(d).StartsWith(".msstyle-import-")), "validation folder left behind");
        }

        [TestMethod]
        public void OutputNextToAnInputIsRefused()
        {
            string output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Aero11)), "refused-" + Guid.NewGuid().ToString("N") + ".msstyles");
            var r = Win7StyleImporter.Run(new Win7ImportOptions
            {
                SourcePath = W7Aero,
                BasePath = W7Aero,
                TemplatePath = Aero11,
                OutputPath = output,
                DryRun = false
            });
            Assert.AreEqual(ImportStatus.Blocked, r.Status);
            // a blocked run never kept a candidate, so its validation can't read as passed even without errors.
            Assert.IsFalse(r.Validation.Passed);
            Assert.IsTrue(r.Errors.Any(e => e.Contains("holds an input")), string.Join("\n", r.Errors));
            Assert.IsFalse(File.Exists(output));
        }

        [TestMethod]
        public void AllocatorSharesIdenticalBytesAndNeverReusesIds()
        {
            byte[] existing = { 1, 2, 3 };
            var alloc = new ResourceIdAllocator(new[]
            {
                Tuple.Create("IMAGE", 500, ResourceInventory.Sha1Hex(existing)),
                Tuple.Create("IMAGE", 900, "deadbeef"),
                Tuple.Create("STREAM", 7, "cafe"),
            });

            Assert.AreEqual(500, alloc.Place("IMAGE", new byte[] { 1, 2, 3 }, out bool reused));
            Assert.IsTrue(reused);
            int a = alloc.Place("IMAGE", new byte[] { 4 }, out reused);
            Assert.IsFalse(reused);
            Assert.AreEqual(901, a);
            Assert.AreEqual(a, alloc.Place("IMAGE", new byte[] { 4 }, out reused));
            Assert.IsTrue(reused);
            Assert.AreEqual(902, alloc.Place("IMAGE", new byte[] { 5 }, out _));
            // types have separate id spaces; same bytes as an IMAGE aren't shared with STREAM.
            Assert.AreEqual(8, alloc.Place("STREAM", new byte[] { 4 }, out reused));
            Assert.IsFalse(reused);
            Assert.AreEqual(3, alloc.Added.Count);

            // an id with several language variants comes in without a hash and is never shared.
            var multi = new ResourceIdAllocator(new[] { Tuple.Create("IMAGE", 10, (string)null) });
            Assert.AreEqual(11, multi.Place("IMAGE", new byte[] { 9 }, out reused));
            Assert.IsFalse(reused);
        }

        static ArgbImage Solid(uint argb)
        {
            var img = new ArgbImage(2, 2);
            for (int i = 0; i < img.Pixels.Length; ++i) img.Pixels[i] = argb;
            return img;
        }

        [TestMethod]
        public void SharedStockSpriteIsOnlyFollowedWhenTheSourceAgrees()
        {
            var same = new Dictionary<string, ArgbImage> { { "B", Solid(0xFF112233) }, { "A", Solid(0xFF112233) } };
            Assert.AreEqual("A", DwmImport.ChooseAlias(new[] { "B", "A" }, n => same[n]));

            var differ = new Dictionary<string, ArgbImage> { { "A", Solid(0xFF112233) }, { "B", Solid(0xFF112234) } };
            Assert.IsNull(DwmImport.ChooseAlias(new[] { "A", "B" }, n => differ[n]));

            Assert.IsNull(DwmImport.ChooseAlias(new[] { "A" }, n => null), "a sprite outside the source atlas can't be used");
        }

        [TestMethod]
        public void NearlyEqualComparesPremultipliedChannels()
        {
            // alpha 1: wildly different rgb is invisible after premultiplication.
            Assert.IsTrue(Win7StyleImporter.NearlyEqual(Solid(0x01FF0000), Solid(0x0100FF00), 1));
            Assert.IsTrue(Win7StyleImporter.NearlyEqual(Solid(0xFF808080), Solid(0xFF818080), 1));
            Assert.IsFalse(Win7StyleImporter.NearlyEqual(Solid(0xFF808080), Solid(0xFF828080), 1));
            Assert.IsFalse(Win7StyleImporter.NearlyEqual(Solid(0xFD808080), Solid(0xFF808080), 1));
            Assert.IsFalse(Win7StyleImporter.NearlyEqual(Solid(0xFF808080), new ArgbImage(2, 3), 255));
        }

        [TestMethod]
        public void GeometryCheckOnlyRejectsEmptyStripsAndOversizedMargins()
        {
            Assert.IsTrue(DwmImport.CheckGeometry(new Rectangle(0, 0, 51, 88), 4, new Margins(20, 6, 20, 8), out _));
            // odd heights are fine, strips are sliced proportionally (dream's 129px top frame in 2).
            Assert.IsTrue(DwmImport.CheckGeometry(new Rectangle(0, 0, 24, 129), 2, new Margins(0, 6, 0, 2), out _));
            // 22px strips, 20px after the inset: 6+8 fits, 12+12 doesn't.
            Assert.IsFalse(DwmImport.CheckGeometry(new Rectangle(0, 0, 51, 88), 4, new Margins(0, 12, 0, 12), out _));
            Assert.IsFalse(DwmImport.CheckGeometry(new Rectangle(0, 0, 30, 88), 4, new Margins(20, 0, 20, 0), out _));
            // frames are vertical only, and 1px comes off every side: 40x8 in 4 strips is 2px tall, nothing left.
            Assert.IsFalse(DwmImport.CheckGeometry(new Rectangle(0, 0, 40, 8), 4, null, out _));
            // vertical strips only: 88x5 in 4 isn't rescued by its width.
            Assert.IsFalse(DwmImport.CheckGeometry(new Rectangle(0, 0, 88, 5), 4, null, out _), "no horizontal split");
        }

        [TestMethod]
        public void OnlyImageFilePropertiesPointAtImageResources()
        {
            var img = new StyleProperty(IDENTIFIER.IMAGEFILE2, IDENTIFIER.FILENAME);
            img.Header.shortFlag = 0x00010000 | 600;
            var r = SemanticModel.ResourceRef(img);
            Assert.AreEqual("IMAGE", r.Item1);
            Assert.AreEqual(600, r.Item2, "uxtheme uses the low word as the resource name");

            // any other FILENAME record is a string id.
            var other = new StyleProperty(IDENTIFIER.CSSNAME, IDENTIFIER.FILENAME);
            other.Header.shortFlag = 600;
            Assert.IsNull(SemanticModel.ResourceRef(other));
            Assert.IsTrue(SemanticModel.IsStringRef(other));

            var stream = new StyleProperty(IDENTIFIER.ATLASIMAGE, IDENTIFIER.STREAM);
            stream.Header.shortFlag = 7;
            Assert.AreEqual("STREAM", SemanticModel.ResourceRef(stream).Item1);

            // IMAGEFILE6 is a string id for win7 uxtheme (6.1.7600) and an image for win10.
            var six = new StyleProperty(IDENTIFIER.IMAGEFILE6, IDENTIFIER.FILENAME);
            six.Header.shortFlag = 42;
            Assert.IsTrue(SemanticModel.IsStringRef(six, Platform.Win7));
            Assert.IsNull(SemanticModel.ResourceRef(six, Platform.Win7));
            Assert.AreEqual("IMAGE", SemanticModel.ResourceRef(six, Platform.Win10).Item1);

            // a STRING with an external id comes from the string table.
            var ext = new StyleProperty(IDENTIFIER.CSSNAME, IDENTIFIER.STRING);
            ext.Header.shortFlag = 7;
            Assert.IsTrue(SemanticModel.IsStringRef(ext));
        }

        [TestMethod]
        public void PaddingAfterAShortStringIsNotDamage()
        {
            // empty string: 2 payload bytes, then 6 bytes of padding up to the next 8-byte record.
            var str = Header(5000, (int)IDENTIFIER.STRING, 2).Concat(new byte[8]).ToArray();
            var next = Header(5001, (int)IDENTIFIER.INT, 4).Concat(new byte[] { 7, 0, 0, 0, 0, 0, 0, 0 }).ToArray();
            var data = str.Concat(next).ToArray();
            int cursor = 0;
            PropertyStream.ReadNextProperty(data, ref cursor);
            Assert.AreEqual(40, cursor);
            var p = PropertyStream.ReadNextProperty(data, ref cursor);
            Assert.AreEqual(7, (int)p.GetValue());
        }

        [TestMethod]
        public void ExcludingDwmWindowKeepsTheTemplateFrames()
        {
            if (!File.Exists(ShinePath)) Assert.Inconclusive("Shine 2.0 not found at " + ShinePath);
            var r = Win7StyleImporter.Run(new Win7ImportOptions
            {
                SourcePath = ShinePath,
                TemplatePath = Aero11,
                DryRun = true,
                Exclude = new List<string> { "DWMWindow|*|*" }
            });
            Assert.AreEqual(ImportStatus.ReadyForVmTest, r.Status, string.Join("\n", r.Errors));
            Assert.AreEqual(r.Dwm.TemplateAtlasId, r.Dwm.NewAtlasId);
            Assert.AreEqual(0, r.Decisions.Count(d => d.Class == "DWMWindow" && d.Action.StartsWith("applied")));
        }

        [TestMethod]
        public void MissingSourceImageIsReportedNotApplied()
        {
            using (var w7 = new VisualStyle())
            {
                w7.Load(W7Aero);
                var model = new SemanticModel(w7);
                var target = new StyleProperty(IDENTIFIER.IMAGEFILE, IDENTIFIER.FILENAME);
                target.SetValue(123);
                var src = new StyleProperty(IDENTIFIER.IMAGEFILE, IDENTIFIER.FILENAME);
                src.SetValue(65000);
                var entry = new SemanticEntry { ResourceType = "IMAGE", ResourceId = 65000, Property = src, TypeId = src.Header.typeID };

                var imp = new Win7StyleImporter(new Win7ImportOptions());
                Assert.IsFalse(imp.ApplySourceValue(model, entry, target, out string err));
                StringAssert.Contains(err, "missing");
                Assert.AreEqual(123, target.Header.shortFlag, "target must stay untouched");
            }
        }

        static byte[] Header(int name, int type, int size)
        {
            var b = new byte[32];
            BitConverter.GetBytes(name).CopyTo(b, 0);
            BitConverter.GetBytes(type).CopyTo(b, 4);
            BitConverter.GetBytes(size).CopyTo(b, 28);
            return b;
        }

        [TestMethod]
        public void OpaqueRecordLongerThanTheStreamIsAnError()
        {
            // FLOAT has no decoder, so the payload is kept as raw bytes; 1000 bytes don't exist here.
            var data = Header(5000, (int)IDENTIFIER.FLOAT, 1000).Concat(new byte[8]).ToArray();
            int cursor = 0;
            Assert.ThrowsException<InvalidDataException>(() => PropertyStream.ReadNextProperty(data, ref cursor));
        }

        [TestMethod]
        public void OpaqueRecordKeepsItsExactBytes()
        {
            var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var data = Header(5000, (int)IDENTIFIER.FLOAT, 8).Concat(payload).Concat(new byte[16]).ToArray();
            int cursor = 0;
            var p = PropertyStream.ReadNextProperty(data, ref cursor);
            Assert.AreEqual(40, cursor);
            CollectionAssert.AreEqual(payload, (byte[])p.GetValue());
        }

        [TestMethod]
        public void GarbageAfterTheLastRecordIsReportedAndConsumed()
        {
            var data = Enumerable.Repeat((byte)0xFF, 64).ToArray();
            int cursor = 0;
            Assert.ThrowsException<PropertyStreamUnknownBytesException>(() => PropertyStream.ReadNextProperty(data, ref cursor));
            Assert.AreEqual(data.Length, cursor, "the reader must move past bytes it can't read or the load loop spins");
        }
    }
}
