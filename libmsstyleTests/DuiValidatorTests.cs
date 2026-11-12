using libmsstyle.Import.Dui;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;

namespace libmsstyleTests
{
    [TestClass]
    public class DuiValidatorTests
    {
        private const string Good = @"<duixml>
	<stylesheets>
		<style resid=""ExplorerBarStyle"">
			<if class=""ExplorerBarBackground"">
				<element background=""dtb(ExplorerBar, 0, 0)"" padding=""rect(12rp, 12rp, 12rp, 12rp)""/>
			</if>
		</style>
	</stylesheets>
	<Element resid=""FolderLayout"" layout=""shellborderlayout()"">
		<scrollviewer sheet=""common"" layoutpos=""top"" height=""91rp"">
			<element sheet=""ExplorerBarStyle"" class=""ExplorerBarBackground"" layoutpos=""top"" layout=""borderlayout()"">
				<Expando layoutpos=""top"" layout=""borderlayout()"" Expanded=""true"">
					<element id=""atom(tasklist)"" layoutpos=""top"" layout=""rowlayout()"">
						<element class=""task_item"" layout=""flowlayout()"" layoutpos=""top"">
							<NavigateButton layout=""borderlayout()"">
								<Button class=""icon"" layoutpos=""left"" content=""icon(22, sysmetric(49), sysmetric(50), library(shell32.dll))"" accessible=""true""/>
								<Button class=""label_normal"" layoutpos=""left"" content=""Switch to Classic View""/>
							</NavigateButton>
						</element>
					</element>
				</Expando>
			</element>
		</scrollviewer>
	</Element>
</duixml>";

        private static DuiValidator.Result Run(params (string name, string text)[] docs)
        {
            var r = new DuiValidator.Result();
            DuiValidator.Validate(docs, null, r);
            return r;
        }

        private static string Errors(DuiValidator.Result r) => string.Join("\n", r.Errors.Select(e => e.Message));

        [TestMethod]
        public void AcceptsAWellFormedShellstyleLayout()
        {
            var r = Run(("SHELL32_UIFILE 18", Good));
            Assert.AreEqual(0, r.Errors.Count, Errors(r));
            // the row is a rowlayout() without a height, which is the shape that actually renders
            Assert.AreEqual(0, r.Warnings.Count, string.Join("\n", r.Warnings.Select(w => w.Message)));
        }

        [TestMethod]
        public void RejectsAnUnknownLayoutConstructor()
        {
            var r = Run(("SHELL32_UIFILE 18", Good.Replace("layout=\"flowlayout()\"", "layout=\"flowlayot()\"")));
            Assert.IsTrue(r.Errors.Any(e => e.Message.Contains("flowlayot") && e.Message.Contains("not a registered layout")), Errors(r));
        }

        [TestMethod]
        public void RejectsALayoutWithTheWrongArgumentCount()
        {
            var r = Run(("SHELL32_UIFILE 18", Good.Replace("layout=\"borderlayout()\"", "layout=\"gridlayout(2)\"")));
            Assert.IsTrue(r.Errors.Any(e => e.Message.Contains("gridlayout") && e.Message.Contains("argument")), Errors(r));
        }

        [TestMethod]
        public void RejectsAnUnknownPlacementToken()
        {
            var r = Run(("SHELL32_UIFILE 18", Good.Replace("layoutpos=\"top\"", "layoutpos=\"middle\"")));
            Assert.IsTrue(r.Errors.Any(e => e.Message.Contains("middle") && e.Message.Contains("placement")), Errors(r));
        }

        [TestMethod]
        public void RejectsATruncatedValueExpression()
        {
            // what a bad string surgery leaves behind: the parser drops the whole resource for this
            var r = Run(("SHELL32_UIFILE 18", Good.Replace("rect(12rp, 12rp, 12rp, 12rp)", "rect(12rp, 12rp, 12rp, 12rp")));
            Assert.IsTrue(r.Errors.Any(e => e.Message.Contains("unbalanced parentheses")), Errors(r));
        }

        [TestMethod]
        public void RejectsAValueFunctionWithTheWrongArgumentCount()
        {
            var r = Run(("SHELL32_UIFILE 18", Good.Replace("dtb(ExplorerBar, 0, 0)", "dtb(ExplorerBar, 0)")));
            Assert.IsTrue(r.Errors.Any(e => e.Message.Contains("dtb()") && e.Message.Contains("argument")), Errors(r));
        }

        [TestMethod]
        public void RejectsAnUnknownFunction()
        {
            var r = Run(("SHELL32_UIFILE 18", Good.Replace("dtb(ExplorerBar, 0, 0)", "dtbx(ExplorerBar, 0, 0)")));
            Assert.IsTrue(r.Errors.Any(e => e.Message.Contains("dtbx") && e.Message.Contains("not a known DUIXML function")), Errors(r));
        }

        [TestMethod]
        public void RejectsAnIdThatIsNotAnAtom()
        {
            var r = Run(("SHELL32_UIFILE 18", Good.Replace("id=\"atom(tasklist)\"", "id=\"tasklist\"")));
            Assert.IsTrue(r.Errors.Any(e => e.Message.Contains("atom(name)")), Errors(r));
        }

        [TestMethod]
        public void RejectsASheetThatResolvesNowhere()
        {
            var r = Run(("SHELL32_UIFILE 18", Good.Replace("sheet=\"ExplorerBarStyle\"", "sheet=\"ExplorerBarStyl\"")));
            Assert.IsTrue(r.Errors.Any(e => e.Message.Contains("ExplorerBarStyl") && e.Message.Contains("no stylesheet")), Errors(r));
        }

        [TestMethod]
        public void AcceptsTheSheetsTheShellProvidesItself()
        {
            // "common" (and the two *placeholder sheets) are registered by shell32 before the theme's markup loads;
            // stock luna references all three without defining them.
            var r = Run(("SHELL32_UIFILE 18", Good));
            Assert.AreEqual(0, r.Errors.Count, Errors(r));
        }

        [TestMethod]
        public void RejectsADuplicateStylesheetResid()
        {
            string dup = Good.Replace("<style resid=\"ExplorerBarStyle\">",
                "<style resid=\"ExplorerBarStyle\"><if class=\"a\"><element/></if></style><style resid=\"ExplorerBarStyle\">");
            var r = Run(("SHELL32_UIFILE 18", dup));
            Assert.IsTrue(r.Errors.Any(e => e.Message.Contains("duplicate stylesheet resid")), Errors(r));
        }

        [TestMethod]
        public void RejectsAStyleWithoutAResid()
        {
            var r = Run(("SHELL32_UIFILE 18", Good.Replace("<style resid=\"ExplorerBarStyle\">", "<style>")));
            Assert.IsTrue(r.Errors.Any(e => e.Message.Contains("without a resid")), Errors(r));
        }

        [TestMethod]
        public void RejectsADocumentThatIsNotWellFormedXml()
        {
            var r = Run(("SHELL32_UIFILE 18", Good.Replace("</duixml>", "")));
            Assert.IsTrue(r.Errors.Any(e => e.Message.Contains("not well-formed XML")), Errors(r));
        }

        [TestMethod]
        public void RejectsARootThatIsNotDuixml()
        {
            var r = Run(("SHELL32_UIFILE 18", Good.Replace("<duixml>", "<ui>").Replace("</duixml>", "</ui>")));
            Assert.IsTrue(r.Errors.Any(e => e.Message.Contains("not <duixml>")), Errors(r));
        }

        [TestMethod]
        public void WarnsAboutARowLayoutWithAFixedHeight()
        {
            // the regression this check exists for: DirectUI skips DoLayout for a rowlayout()'s children when the row
            // carries an explicit height, so the Control Panel band drew its header and none of its links.
            var r = Run(("SHELL32_UIFILE 18", Good.Replace("layout=\"rowlayout()\"", "layout=\"rowlayout()\" height=\"22rp\"")));
            Assert.AreEqual(0, r.Errors.Count, Errors(r));
            Assert.IsTrue(r.Warnings.Any(w => w.Message.Contains("rowlayout()") && w.Message.Contains("zero size")),
                string.Join("\n", r.Warnings.Select(w => w.Message)));
        }

        [TestMethod]
        public void RejectsAGraphicNamingABitmapTheModuleLacks()
        {
            var r = new DuiValidator.Result();
            DuiValidator.Validate(new[] { ("UIFILE 1", Good.Replace("dtb(ExplorerBar, 0, 0)", "graphic(121, 2, -1, 0, 0, 0, 0)")) },
                new HashSet<int> { 10, 11, 12 }, r);
            Assert.IsTrue(r.Errors.Any(e => e.Message.Contains("bitmap 121")), Errors(r));
        }

        [TestMethod]
        public void AcceptsAGraphicWhoseBitmapTheModuleHas()
        {
            var r = new DuiValidator.Result();
            DuiValidator.Validate(new[] { ("UIFILE 1", Good.Replace("dtb(ExplorerBar, 0, 0)", "graphic(121, 2, -1, 0, 0, 0, 0)")) },
                new HashSet<int> { 121 }, r);
            Assert.AreEqual(0, r.Errors.Count, Errors(r));
        }

        [TestMethod]
        public void SheetReferencesResolveAcrossTheWholeModule()
        {
            // UIFILE 1 defines the sheet and a SHELL32_UIFILE layout uses it: resolution is module-wide, not per file.
            string sheets = "<duixml><stylesheets><style resid=\"NavPanelStyle\"><if class=\"x\"><element/></if></style></stylesheets></duixml>";
            string layout = Good.Replace("sheet=\"ExplorerBarStyle\"", "sheet=\"NavPanelStyle\"");
            var r = Run(("UIFILE 1", sheets), ("SHELL32_UIFILE 18", layout));
            Assert.AreEqual(0, r.Errors.Count, Errors(r));
        }
    }
}
