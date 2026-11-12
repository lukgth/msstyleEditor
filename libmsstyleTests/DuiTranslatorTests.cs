using libmsstyle.Import;
using libmsstyle.Import.Dui;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;

namespace libmsstyleTests
{
    [TestClass]
    public class DuiTranslatorTests
    {
        const string Template = @"<duixml>
	<stylesheets>
		<style resid=""ExplorerBarStyle"">
			<Expando background=""argb(0,0,0,0)"" padding=""rect(0,0,0,15rp)"" animation=""RectangleV|s|fast""/>
			<if class=""ExplorerBarBackground"">
				<element padding=""themeable(gtmar(ExplorerBar, 0, 0, 3602), rect(12,12,12,12))"" width=""200rp"" background=""gradient(gtc(ExplorerBar,0,0,3810), gtc(ExplorerBar,0,0,3811), 1)""/>
			</if>
			<if class=""SpecialGroupHeader"">
				<accessiblebutton padding=""themeable(gtmar(ExplorerBar, 12, 0, 3602), rect(12rp,2rp,3rp,0))"" cursor=""hand"" background=""themeable(dtb(ExplorerBar, 12, 0), window)"" contentalign=""middleleft""/>
			</if>
			<if class=""NormalGroupHeader"">
				<accessiblebutton padding=""themeable(gtmar(ExplorerBar, 8, 0, 3602), rect(12rp,2rp,3rp,0))"" cursor=""hand"" background=""themeable(dtb(ExplorerBar, 8, 0), window)"" contentalign=""middleleft""/>
			</if>
			<if class=""SpecialGroupExpand"">
				<Button background=""argb(0,0,0,0)"" content=""themeable(dtb(ExplorerBar, 11, 1), '')""/>
			</if>
			<if class=""NormalGroupExpand"">
				<Button background=""argb(0,0,0,0)"" content=""themeable(dtb(ExplorerBar, 7, 1), '')""/>
			</if>
			<if class=""SpecialGroupBackground"">
				<clipper background=""themeable(dtb(ExplorerBar, 9, 0), window)""/>
			</if>
			<if class=""NormalGroupBackground"">
				<clipper background=""themeable(dtb(ExplorerBar, 5, 0), window)""/>
			</if>
			<if class=""name_special"">
				<button font=""gtf(TextStyle, 3, 0)"" foreground=""themeable(gtc(ExplorerBar, 10, 0, 3803), windowtext)"" padding=""rect(0, 1rp, 0, 0)""/>
			</if>
			<if class=""name_normal"">
				<button font=""gtf(TextStyle, 3, 0)"" foreground=""themeable(gtc(ExplorerBar, 6, 0, 3803), windowtext)"" padding=""rect(0, 1rp, 0, 0)""/>
			</if>
			<if id=""atom(tasklist)"">
				<element padding=""themeable(gtmar(ExplorerBar, 5, 0, 3602), rect(8rp,8rp,8rp,8rp))""/>
			</if>
		</style>
	</stylesheets>
</duixml>";

        sealed class FakeResources : IXpShellResources
        {
            public string ResourceString(int id) { return id == 1 ? "Trebuchet MS" : null; }
            public int? ResourceInt(int id) { return id == 10 ? 700 : (int?)null; }
            public string SystemMetricString(int index) { return index == 6 ? "Tahoma" : null; }
            public string Handlemap(int index) { return index == 1 ? "ScrollBar" : null; }
            public bool HasBitmap(int id) { return true; }
        }

        static XpUifileSyntax.Document StockStylesheet()
        {
            return XpUifileSyntax.Parse(@"<style resid=taskpane>
element [id=atom(sectionlist)] { background: rgb(213,223,242); borderthickness: rect(1,0,0,1); }
</style>
<style resid=sectionss>
expando { margin: rect(0,5,0,5); background: argb(0,0,0,0); }
button [id=atom(header)] { background: rcbmp(112,6,#FF00FF,0,0,1,0); padding: rect(10,0,1,0); foreground: white; }
button [keyfocused] { contentalign: focusrect; }
element [id=atom(title)] { contentalign: middleleft; }
element [id=atom(arrow)] { content: rcbmp(102,2,255,0,0,0,0); }
clipper { alpha: 0; animation: alpha | rectanglev | s | fast; }
clipper [selected] { alpha: 255; }
tasklist [id=atom(tasklist)] { padding: rect(12rp,10rp,12rp,10rp); }
</style>");
        }

        [TestMethod]
        public void ParsesXpStylesheetStructure()
        {
            using (var sh = new XpShellStyle(StockStyles.XpLunaShellPath("NormalColor")))
            {
                var doc = sh.Legacy;
                Assert.IsNotNull(doc);
                Assert.IsTrue(doc.Sheets.Count >= 6, "xp luna shells has 7 stylesheets, got " + doc.Sheets.Count);
                Assert.IsNotNull(doc.Find("taskpane"));
                var rule = doc.Find("taskpane").Rules.FirstOrDefault(r =>
                    r.Selector.Class.Equals("element", StringComparison.OrdinalIgnoreCase)
                    && r.Selector.IdCondition != null && r.Selector.IdCondition.Text.Contains("sectionlist"));
                Assert.IsNotNull(rule, "taskpane element [id=atom(sectionlist)] rule");
                Assert.IsNotNull(rule.Get("background"));
                Assert.AreEqual(0, doc.Warnings.Count, string.Join("\n", doc.Warnings));
            }
        }

        [TestMethod]
        public void ParsesSelectorsConditionsAndQuotedValues()
        {
            var sel = XpUifileSyntax.ParseSelector(@"Button [id=atom(header)][keyfocused][selected=true]");
            Assert.AreEqual("Button", sel.Class);
            Assert.AreEqual("header", sel.IdCondition.Atom);
            Assert.IsTrue(sel.StateConditions.Any(c => c.Name == "keyfocused" && !c.Negated));
            Assert.IsTrue(sel.StateConditions.Any(c => c.Name == "selected" && !c.Negated));

            var doc = XpUifileSyntax.Parse(@"<style resid=x>
element { content: 'a;b}'; background: url('x;y.png'); }
</style>");
            var d = doc.Find("x").Rules.Single().Declarations;
            Assert.AreEqual("'a;b}'", d.Single(x => x.Property == "content").RawValue);
            Assert.AreEqual("url('x;y.png')", d.Single(x => x.Property == "background").RawValue);
        }

        [TestMethod]
        public void LineCommentsDoNotCorruptDeclarationsOrRules()
        {
            // xp's scanner treats // as a comment between any two tokens (scan.l INLINECOMMENT); cpstyle_common.uix
            // writes `padding: rect(1,1,1,1);  // For focus rect`.
            var doc = XpUifileSyntax.Parse(@"<style resid=x>
// a comment before a rule
element
{
    alpha: 255;  // For focus rect
    // a comment before a declaration
    background: rgb(1,2,3);
}
</style>");
            Assert.AreEqual(0, doc.Warnings.Count, string.Join("\n", doc.Warnings));
            var rule = doc.Find("x").Rules.Single();
            Assert.AreEqual("255", rule.Get("alpha"));
            Assert.AreEqual("rgb(1,2,3)", rule.Get("background"));
            Assert.AreEqual(2, rule.Declarations.Count);
        }

        [TestMethod]
        public void NotEqualConditionsKeepTheOperatorOutOfTheName()
        {
            // parse.y: '[' YYIDENT '!' '=' value ']' is PALOGOP_NotEqual, not a property called "enabled!".
            var sel = XpUifileSyntax.ParseSelector("button [enabled!=false]");
            var cond = sel.StateConditions.Single();
            Assert.AreEqual("enabled", cond.Name);
            Assert.IsTrue(cond.Negated);
            Assert.AreEqual("false", cond.Text);
        }

        [TestMethod]
        public void TranslatesValuesToDuiXml()
        {
            var report = new TranslationReport();
            var res = new FakeResources();
            Assert.AreEqual("graphic(110, 6, 16711935, 0, 0, 1, 0)",
                XpShellTranslator.TranslateValue("rcbmp(110,6,#FF00FF,0,0,1,0)", res, report, "t"));
            Assert.AreEqual("'Tahoma'", XpShellTranslator.TranslateValue("sysmetricstr(6)", res, report, "t"));
            Assert.AreEqual("700", XpShellTranslator.TranslateValue("rcint(10)", res, report, "t"));
            Assert.AreEqual("'Trebuchet MS'", XpShellTranslator.TranslateValue("rcstr(1)", res, report, "t"));
            Assert.AreEqual("dtb(ScrollBar, 1, 2)",
                XpShellTranslator.TranslateValue("dtb(handlemap(1), 1, 2)", res, report, "t"));
            Assert.AreEqual("gradient(argb(0, 123, 162, 231), argb(0, 99, 117, 214), 1)",
                XpShellTranslator.TranslateValue("gradient(argb(0,123,162,231), argb(0,99,117,214), 1)", res, report, "t"));
            Assert.AreEqual(0, report.Unsupported.Count, string.Join("\n", report.Unsupported));
        }

        [TestMethod]
        public void StockSourceLeavesTemplateUntouched()
        {
            var template = DuiXmlDocument.Parse(Template);
            string before = template.ToXml();
            var report = XpShellTranslator.Translate(StockStylesheet(), StockStylesheet(), template, new FakeResources());
            Assert.AreEqual(0, report.RulesChanged);
            Assert.AreEqual(before, template.ToXml());
        }

        [TestMethod]
        public void LegacyMarkupIsKeptVerboseFree()
        {
            string text = @"<style resid=x>
element { background: argb(0,0,0,0); }
</style>
<element resid=main id=atom(main) sheet=styleref(main) layout=borderlayout()>
<ScrollViewer id=atom(scroller) sheet=styleref(taskpane) layoutpos=top height=54>
<element id=atom(sectionlist) layout=gridlayout(0,0,3)/>
</ScrollViewer>
</element>";
            var doc = XpUifileSyntax.Parse(text);
            Assert.AreEqual(0, doc.Warnings.Count, string.Join("\n", doc.Warnings));
            Assert.AreEqual(1, doc.Sheets.Count);
            Assert.IsTrue(doc.MarkupText.Contains("gridlayout(0,0,3)"), doc.MarkupText);
        }

        [TestMethod]
        public void ChangedRulesAreMappedOntoTemplateClasses()
        {
            var stock = StockStylesheet();
            var source = XpUifileSyntax.Parse(@"<style resid=taskpane>
element [id=atom(sectionlist)] { background: rgb(1,2,3); }
</style>
<style resid=sectionss>
expando { margin: rect(0,7,0,7); animation: rectangle | s | mediumfast; }
button [id=atom(header)] { background: rcbmp(110,6,#FF00FF,0,0,1,0); }
clipper [selected] { alpha: 128; }
</style>");
            var template = DuiXmlDocument.Parse(Template);
            var report = XpShellTranslator.Translate(source, stock, template, new FakeResources());

            var sheet = template.FindStyle("ExplorerBarStyle");
            var pane = DuiXmlDocument.Child(template.FindClassBlock(sheet, "ExplorerBarBackground"), "element");
            Assert.AreEqual("rgb(1, 2, 3)", (string)pane.Attribute("background"));
            Assert.AreEqual("rect(0, 7, 0, 7)", (string)DuiXmlDocument.Child(sheet, "Expando").Attribute("padding"));
            // `rectangle` and `rectanglev` animate different areas in xp, so the token is passed through as written.
            Assert.AreEqual("rectangle|s|mediumfast", (string)DuiXmlDocument.Child(sheet, "Expando").Attribute("animation"));

            // sectionss styles the non-main sections, which the template keys as Normal*.
            var header = DuiXmlDocument.Child(template.FindClassBlock(sheet, "NormalGroupHeader"), "accessiblebutton");
            Assert.AreEqual("graphic(110, 6, 16711935, 0, 0, 1, 0)", (string)header.Attribute("background"));

            // the clipper's open state is Expanded in the win10 tree, not selected.
            var bg = template.FindClassBlock(sheet, "NormalGroupBackground");
            var cond = bg.Elements("if").Single(e => DuiXmlDocument.AttributeIs(e, "Expanded", "true"));
            Assert.AreEqual("128", (string)DuiXmlDocument.Child(cond, "clipper").Attribute("alpha"));

            Assert.IsTrue(report.RulesTranslated >= 4, report.ToString());
            Assert.IsTrue(report.Unsupported.Count == 0, string.Join("\n", report.Unsupported));
            // The translated document must still be valid DUIXML.
            DuiXmlDocument.Parse(template.ToXml());
        }
        [TestMethod]
        public void IdenticalUnsupportedFamilyIsNotReportedAsLoss()
        {
            var stock = XpUifileSyntax.Parse("<style resid=scrollerss>scrollbar { background: rgb(1,2,3); }</style>");
            var template = DuiXmlDocument.Parse(Template);
            var report = XpShellTranslator.Translate(stock, stock, template, new FakeResources());
            Assert.AreEqual(1, report.RulesIdentical);
            Assert.AreEqual(0, report.Unsupported.Count);
            var changed = XpUifileSyntax.Parse("<style resid=scrollerss>scrollbar { background: rgb(4,5,6); }</style>");
            report = XpShellTranslator.Translate(changed, stock, template, new FakeResources());
            Assert.AreEqual(1, report.Unsupported.Count);
            var existing = DuiXmlDocument.Parse("<duixml><stylesheets><style resid='scrollerss'><scrollbar background='old'/></style></stylesheets></duixml>");
            report = XpShellTranslator.Translate(changed, stock, existing, new FakeResources());
            Assert.AreEqual(0, report.Unsupported.Count, string.Join("\n", report.Unsupported));
            Assert.AreEqual("rgb(4, 5, 6)", (string)DuiXmlDocument.Child(existing.FindStyle("scrollerss"), "scrollbar").Attribute("background"));
        }

        [TestMethod]
        public void AuxiliaryRulesReachExistingConsumerElements()
        {
            var source = XpUifileSyntax.Parse(@"<style resid=sectiontaskss>element [id=atom(title)] { foreground: rgb(1,2,3); }</style>
<style resid=main>duilistview [id=atom(listview)] { background: rgb(4,5,6); }</style>
<style resid=namespaceiteminfolist>
namespaceiteminfo [id=atom(InfoName)] { foreground: rgb(7,8,9); }
namespaceiteminfo [id=atom(InfoType)] { foreground: rgb(10,11,12); }
</style>
<style resid=categorylistitemss>linkelement [mousefocused] { foreground: rgb(13,14,15); }</style>");
            var template = DuiXmlDocument.Parse(@"<duixml><stylesheets>
<style resid='ExplorerBarStyle'><if class='label_normal'><Button foreground='old'/></if></style>
<style resid='main'><if id='atom(listview)'><DUIListView background='old'/></if></style>
<style resid='Preview'><if id='atom(PreviewMetadataTitleControl)'><MetadataElement foreground='old'/></if>
<if id='atom(PreviewMetadataSubTitleControl)'><MetadataElement foreground='old'/></if></style>
<style resid='CategoryPanelStyle'><if id='atom(name)'><ControlPanelLink foreground='old'/></if></style>
</stylesheets></duixml>");
            var report = XpShellTranslator.Translate(source, null, template, new FakeResources());
            Assert.AreEqual(0, report.Unsupported.Count, string.Join("\n", report.Unsupported));
            var sheet = template.FindStyle("ExplorerBarStyle");
            Assert.AreEqual("rgb(1, 2, 3)", (string)DuiXmlDocument.Child(template.FindClassBlock(sheet, "label_normal"), "Button").Attribute("foreground"));
            Assert.AreEqual("rgb(4, 5, 6)", (string)DuiXmlDocument.Child(template.FindIdBlock(template.FindStyle("main"), "listview"), "DUIListView").Attribute("background"));
            Assert.AreEqual("rgb(7, 8, 9)", (string)DuiXmlDocument.Child(template.FindIdBlock(template.FindStyle("Preview"), "PreviewMetadataTitleControl"), "MetadataElement").Attribute("foreground"));
            Assert.AreEqual("rgb(10, 11, 12)", (string)DuiXmlDocument.Child(template.FindIdBlock(template.FindStyle("Preview"), "PreviewMetadataSubTitleControl"), "MetadataElement").Attribute("foreground"));
            var link = template.FindIdBlock(template.FindStyle("CategoryPanelStyle"), "name");
            Assert.IsTrue(link.Descendants("ControlPanelLink").Any(e => (string)e.Attribute("foreground") == "rgb(13, 14, 15)"));
            DuiXmlDocument.Parse(template.ToXml());
        }

        [TestMethod]
        public void RepeatedStockSelectorsMergeBeforeChangeClassification()
        {
            var stock = XpUifileSyntax.Parse("<style resid=scrollerss>scrollbar { foreground: rgb(1,2,3); } scrollbar { background: rgb(4,5,6); }</style>");
            var source = XpUifileSyntax.Parse("<style resid=scrollerss>scrollbar { foreground: rgb(1,2,3); background: rgb(4,5,6); }</style>");
            var report = XpShellTranslator.Translate(source, stock, DuiXmlDocument.Parse(Template), new FakeResources());
            Assert.AreEqual(1, report.RulesIdentical);
            Assert.AreEqual(0, report.RulesChanged);
            Assert.AreEqual(0, report.Unsupported.Count);
        }

        [TestMethod]
        public void AuxiliaryFontUsesResolvedGtfRecordOnExistingTarget()
        {
            var source = XpUifileSyntax.Parse("<style resid=categorylistitemss>linkelement { fontface: 'Tahoma'; fontsize: 9; }</style>");
            var template = DuiXmlDocument.Parse("<duixml><stylesheets><style resid='CategoryPanelStyle'><if id='atom(name)'><ControlPanelLink/></if></style></stylesheets></duixml>");
            const string font = "gtf(ControlPanelStyle, 1, 0, 210)";
            var fonts = new System.Collections.Generic.Dictionary<string, string>
            { { XpShellTranslator.FontKey("categorylistitemss", "linkelement", null, false), font } };
            var report = XpShellTranslator.Translate(source, null, template, new FakeResources(), auxiliaryFonts: fonts);
            Assert.AreEqual(0, report.Unsupported.Count, string.Join("\n", report.Unsupported));
            Assert.AreEqual(1, report.RulesSpecialized);
            Assert.AreEqual(font, (string)DuiXmlDocument.Child(template.FindIdBlock(template.FindStyle("CategoryPanelStyle"), "name"), "ControlPanelLink").Attribute("font"));
        }

    }
}
