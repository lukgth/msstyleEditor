using libmsstyle.Import.Dui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace libmsstyleTests
{
    [TestClass]
    public class LegacyPaneMarkupTests
    {
        private const string TopBand = @"
<element resid=main id=atom(main) sheet=styleref(main) layout=borderlayout()>
<ScrollViewer id=atom(scroller) sheet=styleref(taskpane) layoutpos=bottom height=0>
</ScrollViewer>
<ScrollViewer id=atom(scroller) sheet=styleref(taskpane) xscrollable=false layoutpos=top height=54>
<element id=atom(sectionlist) layout=gridlayout(0,0,3)/>
</ScrollViewer>
<Element layoutpos=client layout=filllayout()>
<Element id=atom(clientviewhost) layout=borderlayout() height=300>
</Element>
</Element>
</element>
<expando resid=mainsection layout=borderlayout() width=300rp>
<element layoutpos=top layout=filllayout()>
<button id=atom(header) layoutpos=top layout=borderlayout()>
<element id=atom(title) layoutpos=left/>
<element id=atom(arrow) layoutpos=right/>
</button>
<element id=atom(icon) layoutpos=left/>
</element>
<clipper layoutpos=top>
<element id=atom(watermark) layoutpos=top layout=borderlayout() height=22rp>
<tasklist id=atom(tasklist) layout=flowlayout(0,0,0)/>
</element>
</clipper>
</expando>";

        private const string BottomBand = @"
<element resid=main id=atom(main) layout=borderlayout()>
<ScrollViewer layoutpos=bottom height=70><element id=atom(sectionlist) layout=gridlayout(0,0,3)/></ScrollViewer>
</element>
<expando resid=mainsection layout=flowlayout(0,0,0) width=1200rp>
<button id=atom(header) layoutpos=top layout=flowlayout() width=60 height=60></button>
<clipper layoutpos=bottom><element id=atom(watermark) layoutpos=bottom height=48rp><tasklist id=atom(tasklist)/></element></clipper>
</expando>";

        private const string LeftSidebar = @"
<element resid=main id=atom(main) layout=borderlayout()>
<ScrollViewer layoutpos=left width=200>
<element id=atom(sectionlist) layout=verticalflowlayout(0,0,3)/>
</ScrollViewer>
<Element layoutpos=Client layout=filllayout()><Element id=atom(clientviewhost)/></Element>
</element>";

        [TestMethod]
        public void ReadsTopBandPlacementAndSectionGeometry()
        {
            var pane = LegacyPaneLayout.From(TopBand);
            Assert.AreEqual(LegacyPanePlacement.Top, pane.Placement);
            Assert.AreEqual("top", pane.PlacementText);
            Assert.AreEqual(54, pane.BandHeight);
            Assert.AreEqual("gridlayout(0,0,3)", pane.SectionListLayout);
            Assert.AreEqual(300, pane.ExpandoWidth);
            Assert.AreEqual(22, pane.WatermarkHeight);
            Assert.AreEqual("top", pane.HeaderLayoutPos);
            Assert.IsTrue(pane.IsBand);
        }

        [TestMethod]
        public void ReadsBottomBandAndUnquotedAttributes()
        {
            var pane = LegacyPaneLayout.From(BottomBand);
            Assert.AreEqual(LegacyPanePlacement.Bottom, pane.Placement);
            Assert.AreEqual(70, pane.BandHeight);
            Assert.AreEqual(1200, pane.ExpandoWidth);
            Assert.AreEqual(48, pane.WatermarkHeight);
        }

        [TestMethod]
        public void ReadsLeftSidebarWithoutExpando()
        {
            var pane = LegacyPaneLayout.From(LeftSidebar);
            Assert.AreEqual(LegacyPanePlacement.Left, pane.Placement);
            Assert.AreEqual("verticalflowlayout(0,0,3)", pane.SectionListLayout);
            Assert.AreEqual(0, pane.ExpandoWidth);
            Assert.IsFalse(pane.IsBand);
        }

        [TestMethod]
        public void MissingOrEmptyMarkupIsUnknown()
        {
            Assert.AreEqual(LegacyPanePlacement.Unknown, LegacyPaneLayout.From(null).Placement);
            Assert.AreEqual(LegacyPanePlacement.Unknown, LegacyPaneLayout.From("").Placement);
            Assert.AreEqual(LegacyPanePlacement.Unknown, LegacyPaneLayout.From("<style resid=main></style>").Placement);
        }

        [TestMethod]
        public void ParsesQuotedValuesCommentsAndSelfClosingTags()
        {
            var root = LegacyMarkup.Parse(@"<!-- x <element resid=main> -->
<element resid=""main"" layout=""borderlayout()"">
  <button content=""Music Tasks"" id=atom(header)/>
  <element id=atom(sectionlist) />
</element>");
            var main = root.Descendants().GetEnumerator();
            Assert.IsTrue(main.MoveNext());
            Assert.AreEqual("element", main.Current.Name);
            Assert.AreEqual("main", main.Current["resid"]);
            Assert.AreEqual("button", main.Current.Children[0].Name);
            Assert.AreEqual("Music Tasks", main.Current.Children[0]["content"]);
            Assert.AreEqual("element", main.Current.Children[1].Name);
        }

        [TestMethod]
        public void LineCommentsAndGluedAttributesDoNotBecomeElements()
        {
            // folder.uix comments elements out with //, and writes id=atom(listview)layoutpos=client with the next
            // attribute glued on; a commented-out section list must not decide the pane's placement.
            var pane = LegacyPaneLayout.From(@"
<element resid=main layout=borderlayout()>
//<ScrollViewer layoutpos=bottom height=0><element id=atom(sectionlist)/></ScrollViewer>
<ScrollViewer layoutpos=left width=200>
<element id=atom(sectionlist)layout=gridlayout(0,0,3)/>
</ScrollViewer>
</element>");
            Assert.AreEqual(LegacyPanePlacement.Left, pane.Placement);
            Assert.AreEqual("gridlayout(0,0,3)", pane.SectionListLayout);
        }

        [TestMethod]
        public void IdMatchIsTheWholeAtomNotASubstring()
        {
            // atom(sectionlist2) is a different element; matching it would move the pane to a band xp never asked for.
            var pane = LegacyPaneLayout.From(@"
<element resid=main layout=borderlayout()>
<ScrollViewer layoutpos=top height=54><element id=atom(sectionlist2) layout=gridlayout(0,0,3)/></ScrollViewer>
</element>");
            Assert.AreEqual(LegacyPanePlacement.Unknown, pane.Placement);
            Assert.IsNull(pane.SectionListLayout);
        }

        [TestMethod]
        public void LengthsRejectJunkAndHonourTheirUnit()
        {
            var rp = LegacyPaneLayout.From(@"
<element resid=main layout=borderlayout()>
<ScrollViewer layoutpos=top height=54rp><element id=atom(sectionlist)/></ScrollViewer>
</element>");
            Assert.AreEqual(54, rp.BandHeight);

            var junk = LegacyPaneLayout.From(@"
<element resid=main layout=borderlayout()>
<ScrollViewer layoutpos=top height=300xyz><element id=atom(sectionlist)/></ScrollViewer>
</element>");
            Assert.AreEqual(0, junk.BandHeight);
        }

        [TestMethod]
        public void ReadsTheTreeOfWhicheverDocumentHasOne()
        {
            // a shellstyle can hold two UIFILEs; xp's control panel pane tree lives in the second one.
            var pane = LegacyPaneLayout.FromDocuments(new[]
            {
                "<style resid=cpstyle></style>",
                "<element resid=main layout=borderlayout()><ScrollViewer layoutpos=bottom height=70><element id=atom(sectionlist)/></ScrollViewer></element>",
            });
            Assert.AreEqual(LegacyPanePlacement.Bottom, pane.Placement);
            Assert.AreEqual(70, pane.BandHeight);
        }
    }
}
