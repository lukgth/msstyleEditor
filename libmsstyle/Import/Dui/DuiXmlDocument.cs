using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace libmsstyle.Import.Dui
{
    /// <summary>
    /// A Vista+ shellstyle UIFILE 1 (DUIXML) as an editable tree. The template is well formed XML,
    /// so it is loaded with System.Xml and written back with the template's own indentation (tabs,
    /// CRLF). Shellstyle layout edits must go through this class: the sheets are XML, and literal
    /// string surgery on attributes breaks as soon as an attribute order or quote style differs.
    /// </summary>
    public sealed class DuiXmlDocument
    {
        public XDocument Document { get; private set; }
        public List<string> Warnings { get; private set; }

        private DuiXmlDocument(XDocument doc)
        {
            Document = doc;
            Warnings = new List<string>();
        }

        public static DuiXmlDocument Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            try
            {
                var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
                if (doc.Root == null || !string.Equals(doc.Root.Name.LocalName, "duixml", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("not a DUIXML document (root element is not <duixml>)");
                return new DuiXmlDocument(doc);
            }
            catch (XmlException e)
            {
                throw new InvalidDataException("the shellstyle UIFILE is not valid DUIXML: " + e.Message, e);
            }
        }

        public XElement Root { get { return Document.Root; } }

        private XElement Stylesheets
        {
            get { return Root.Element("stylesheets") ?? Root.Element("styleSheets"); }
        }

        public IEnumerable<XElement> Styles
        {
            get
            {
                var sheets = Stylesheets;
                return sheets == null ? System.Linq.Enumerable.Empty<XElement>() : sheets.Elements("style");
            }
        }

        public XElement FindStyle(string resid)
        {
            foreach (var s in Styles)
                if (string.Equals((string)s.Attribute("resid"), resid, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        /// <summary>Serialize with the template's layout: no XML declaration, tab indentation, CRLF.</summary>
        public string ToXml()
        {
            var sb = new StringBuilder();
            var settings = new XmlWriterSettings
            {
                OmitXmlDeclaration = true,
                Indent = true,
                IndentChars = "\t",
                NewLineChars = "\r\n",
                NewLineHandling = NewLineHandling.Replace,
                Encoding = new UTF8Encoding(false),
            };
            using (var w = XmlWriter.Create(sb, settings))
                Document.Save(w);
            return sb.ToString();
        }

        // ---- queries ----------------------------------------------------------------------

        /// <summary>The <c>&lt;if class="name"&gt;</c> block in a style sheet, or null.</summary>
        public XElement FindClassBlock(XElement style, string className)
        {
            if (style == null) return null;
            foreach (var e in style.Elements("if"))
                if (AttributeIs(e, "class", className)) return e;
            return null;
        }

        /// <summary>The <c>&lt;if id="atom(name)"&gt;</c> block in a style sheet, or null.</summary>
        public XElement FindIdBlock(XElement style, string atom)
        {
            if (style == null) return null;
            foreach (var e in style.Elements("if"))
            {
                string id = (string)e.Attribute("id");
                if (id != null && string.Equals(id.Trim(), "atom(" + atom + ")", StringComparison.OrdinalIgnoreCase)) return e;
            }
            return null;
        }

        /// <summary>The direct child element with this name (case-insensitive), or null.</summary>
        public static XElement Child(XElement parent, string localName)
        {
            if (parent == null) return null;
            foreach (var e in parent.Elements())
                if (string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        public static IEnumerable<XElement> Children(XElement parent, string localName)
        {
            if (parent == null) yield break;
            foreach (var e in parent.Elements())
                if (string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase)) yield return e;
        }

        public static bool AttributeIs(XElement e, string name, string value)
        {
            if (e == null) return false;
            var a = Attribute(e, name);
            return a != null && string.Equals(a.Value, value, StringComparison.OrdinalIgnoreCase);
        }

        public static XAttribute Attribute(XElement e, string name)
        {
            if (e == null) return null;
            foreach (var a in e.Attributes())
                if (string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }

        /// <summary>Set an attribute regardless of the existing spelling of its name.</summary>
        public static void SetAttribute(XElement e, string name, string value)
        {
            if (e == null || name == null || value == null) return;
            var a = Attribute(e, name);
            if (a != null)
            {
                a.Value = value;
                return;
            }
            e.SetAttributeValue(name, value);
        }

        public static bool RemoveAttribute(XElement e, string name)
        {
            var a = Attribute(e, name);
            if (a == null) return false;
            a.Remove();
            return true;
        }

        /// <summary>A nested condition inside a class block: <c>&lt;if MouseFocused="true"&gt;</c>. Creates it
        /// (as the last child) when absent.</summary>
        public XElement EnsureCondition(XElement parent, string condition, string value = "true")
        {
            if (parent == null) return null;
            foreach (var e in parent.Elements("if"))
                if (AttributeIs(e, condition, value)) return e;
            var created = new XElement("if");
            created.SetAttributeValue(condition, value);
            parent.Add(created);
            return created;
        }

        /// <summary>Find the first child element inside a block whose tag is <paramref name="tag"/>, else create it
        /// before any nested &lt;if&gt; so plain declarations stay first.</summary>
        public static XElement EnsureElement(XElement parent, string tag)
        {
            var existing = Child(parent, tag);
            if (existing != null) return existing;
            if (parent == null) return null;
            var created = new XElement(tag);
            var firstIf = Child(parent, "if");
            if (firstIf != null) firstIf.AddBeforeSelf(created);
            else parent.Add(created);
            return created;
        }

        /// <summary>Ensure a child element exists with the given attributes; returns it.</summary>
        public static XElement AddElement(XElement parent, string tag, params (string name, string value)[] attrs)
        {
            var e = new XElement(tag);
            foreach (var a in attrs) if (a.value != null) e.SetAttributeValue(a.name, a.value);
            parent.Add(e);
            return e;
        }
    }
}
