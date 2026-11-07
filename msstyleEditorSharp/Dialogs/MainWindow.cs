using libmsstyle;
using msstyleEditor.Dialogs;
using msstyleEditor.Properties;
using msstyleEditor.PropView;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;

namespace msstyleEditor
{
    public partial class MainWindow : Form
    {
        private VisualStyle m_style;
        private Selection m_selection = new Selection();
        private StyleResource m_selectedImage = null;
        private Rectangle? m_selectedCropRect = null;
        private int m_selectedStateIndex = -1;
        private int m_selectedImageCount = 0;
        private int m_selectedImageLayout = 0;
        private ThemeManager m_themeManager = null;

        private SearchDialog m_searchDialog = new SearchDialog();
        private ClassViewWindow m_classView;
        private PropertyViewWindow m_propertyView;
        private ImageView m_imageView;
        private RenderView m_renderView;

        private TimingFunction m_selectedTimingFunction;
        private AnimationTypeDescriptor m_selectedAnimation;

        // Tracks class IDs that were added during the current session
        // These classes should be available as base class options even if they
        // don't have entries in the original BCMAP structure.
        private HashSet<int> m_sessionAddedClassIds = new HashSet<int>();

        public MainWindow(String[] args)
        {
            InitializeComponent();
            ApplyDestructiveButtonStyle();

            // Our ribbon "SystemColors" theme adapts itself to the active visual style
            ribbonMenu.ThemeColor = RibbonTheme.SystemColors;

            // Set the best matching theme dock theme. This can only be done when no windows
            // were added to the dock yet.
            float brightness = SystemColors.Control.GetBrightness();
            dockPanel.Theme = brightness < 0.5f
                ? dockPanel.Theme = new WeifenLuo.WinFormsUI.Docking.VS2015DarkTheme()
                : dockPanel.Theme = new WeifenLuo.WinFormsUI.Docking.VS2015LightTheme();

            OnSystemColorsChanged(null, null);

            m_classView = new ClassViewWindow();
            m_classView.Show(dockPanel, DockState.DockLeft);
            m_classView.CloseButtonVisible = false;
            m_classView.OnSelectionChanged += OnTreeItemSelected;

            m_imageView = new ImageView();
            m_imageView.SelectedIndexChanged += OnImageSelectIndex;
            m_imageView.OnViewBackColorChanged += OnImageViewBackgroundChanged;
            m_imageView.ZoomChanged += OnImageViewZoomChanged;
            m_imageView.Show(dockPanel, DockState.Document);
            m_imageView.VisibleChanged += (s, e) => { btShowImageView.Checked = m_imageView.Visible; };
            m_imageView.SetActiveTabs(-1, 0);

            m_renderView = new RenderView();
            m_renderView.Show(m_imageView.Pane, DockAlignment.Top, 0.25);
            m_renderView.VisibleChanged += (s, e) => { btShowRenderView.Checked = m_renderView.Visible; };
            m_renderView.Visible = false;
            m_renderView.Hide();

            m_propertyView = new PropertyViewWindow();
            m_propertyView.Show(dockPanel, DockState.DockRight);
            m_propertyView.CloseButtonVisible = false;
            m_propertyView.OnPropertyAdded += OnPropertyAdded;
            m_propertyView.OnPropertyRemoved += OnPropertyRemoved;

            try
            {
                m_themeManager = new ThemeManager();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message + "\r\nIs Aero/DWM disabled? Starting without \"Test Theme\" feature.",
                    "Themeing API unavailable",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                btTestTheme.Enabled = false;
            }

            SetTestThemeButtonState(false);
            CloseStyle();

            m_searchDialog.StartPosition = FormStartPosition.CenterParent;
            m_searchDialog.OnSearch += this.OnSearchNextItem;
            m_searchDialog.OnReplace += this.OnReplaceItem;

            if (args.Length > 0)
            {
                OpenStyle(args[0]);
            }
        }

        #region State Management & Helper

        private void SetTestThemeButtonState(bool active)
        {
            if (active)
            {
                btTestTheme.LargeImage = Resources.Stop32;
                btTestTheme.Text = "Stop Test";
                btReloadTest.Enabled = true;
            }
            else
            {
                btTestTheme.LargeImage = Resources.Play32;
                btTestTheme.Text = "Test";
                btReloadTest.Enabled = false;
            }
        }

        private void ApplyDestructiveButtonStyle()
        {
            var destructiveColor = Color.FromArgb(210, 65, 65);
            TintRibbonButton(btPropertyRemove, destructiveColor);
            TintRibbonButton(btRemoveClass, destructiveColor);
            TintRibbonButton(btRemovePart, destructiveColor);
            TintRibbonButton(btRemoveState, destructiveColor);
        }

        private static void TintRibbonButton(System.Windows.Forms.RibbonButton button, Color tint)
        {
            if (button == null)
                return;

            if (button.Image != null)
                button.Image = TintImage(button.Image, tint);
            if (button.LargeImage != null)
                button.LargeImage = TintImage(button.LargeImage, tint);
            if (button.SmallImage != null)
                button.SmallImage = TintImage(button.SmallImage, tint);
        }

        private static Image TintImage(Image source, Color tint)
        {
            if (source == null)
                return null;

            float r = tint.R / 255f;
            float g = tint.G / 255f;
            float b = tint.B / 255f;

            var tinted = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (var gfx = Graphics.FromImage(tinted))
            using (var attrs = new ImageAttributes())
            {
                var matrix = new ColorMatrix(new[]
                {
                    new[] { r, 0f, 0f, 0f, 0f },
                    new[] { 0f, g, 0f, 0f, 0f },
                    new[] { 0f, 0f, b, 0f, 0f },
                    new[] { 0f, 0f, 0f, 1f, 0f },
                    new[] { 0f, 0f, 0f, 0f, 1f },
                });

                attrs.SetColorMatrix(matrix);
                gfx.DrawImage(source,
                    new Rectangle(0, 0, tinted.Width, tinted.Height),
                    0,
                    0,
                    source.Width,
                    source.Height,
                    GraphicsUnit.Pixel,
                    attrs);
            }

            return tinted;
        }

        private string BuildClassInheritanceStatusText(StyleClass cls)
        {
            if (cls == null)
            {
                return "No selection";
            }

            if (m_style != null)
            {
                int effectiveBaseClassId = m_style.GetEffectiveBaseClassId(cls);
                if (effectiveBaseClassId >= 0)
                {
                    if (m_style.Classes.TryGetValue(effectiveBaseClassId, out StyleClass baseClass))
                    {
                        string inheritanceMode = cls.BaseClassId >= 0 ? "inherits from" : "implicitly inherits from";
                        return $"{cls.ClassName} (#{cls.ClassId}) {inheritanceMode} {baseClass.ClassName} (#{baseClass.ClassId})";
                    }

                    return $"{cls.ClassName} (#{cls.ClassId}) inherits from Unknown (#{effectiveBaseClassId})";
                }
            }

            return $"{cls.ClassName} (#{cls.ClassId}) has no base class";
        }

        private void UpdateItemSelection(StyleClass cls = null, StylePart part = null, StyleState state = null, int resId = -1)
        {
            m_selection.Class = cls;
            m_selection.Part = part;
            m_selection.State = state;
            m_selection.ResourceId = resId;

            lbStatusMessage.Text = BuildClassInheritanceStatusText(cls);
        }

        private void UpdateWindowCaption(string text)
        {
            this.Text = "msstyleEditor - " + text;
        }

        private void UpdateStatusText(string text)
        {
            lbStatusMessage.Text = text;
        }

        private void UpdateImageInfo(Image img)
        {
            if (img == null)
            {
                lbImageInfo.Visible = false;
            }
            else
            {
                lbImageInfo.Text = $"{img.Width}x{img.Height}px";
                lbImageInfo.Visible = true;
            }
        }

        private void UpdateZoomInfo(Image img, float zoomFactor)
        {
            if (img == null)
            {
                lbImageZoom.Visible = false;
            }
            else
            {
                lbImageZoom.Text = $"{(int)Math.Round(zoomFactor * 100.0f)}%";
                lbImageZoom.Visible = true;
            }
        }

        private void OpenStyle(string path)
        {
            try
            {
                m_style = new VisualStyle();
                m_style.Load(path);

                // If XP style with multiple color schemes, ask user to pick one
                if (m_style.Platform == Platform.WinXP &&
                    m_style.XpColorSchemes != null &&
                    m_style.XpColorSchemes.Count > 1)
                {
                    using (var dlg = new ColorSchemeDialog(m_style.XpColorSchemes))
                    {
                        if (dlg.ShowDialog(this) == DialogResult.OK)
                        {
                            m_style.LoadXpWithScheme(dlg.SelectedScheme);
                        }
                        else
                        {
                            return;
                        }
                    }
                }

                UpdateWindowCaption(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Are you sure this is a Windows XP, Vista or higher visual style?\r\n\r\nDetails: {ex.Message}", "Error loading style!",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            m_classView.SetVisualStyle(m_style);

            bool isXp = m_style.Platform == Platform.WinXP;
            btFileSave.Enabled = !isXp;
            btFileInfoExport.Enabled = true;
            btImageExport.Enabled = false;
            btImageImport.Enabled = false;
            btPropertyAdd.Enabled = !isXp;
            btPropertyRemove.Enabled = !isXp;
            btAddClass.Enabled = !isXp;
            btAddPart.Enabled = !isXp;
            btAddState.Enabled = !isXp;
            btRemoveClass.Enabled = !isXp;
            btRemovePart.Enabled = !isXp;
            btRemoveState.Enabled = !isXp;
            btTestTheme.Enabled = !isXp && m_themeManager != null;

            if (m_style.PreferredStringTable?.Count == 0)
                btFileSave.Style = RibbonButtonStyle.Normal;
            else btFileSave.Style = RibbonButtonStyle.SplitDropDown;

            lbStylePlatform.Text = m_style.Platform.ToDisplayString();

            if (m_style.Platform != Platform.WinXP && m_style.PreferredStringTable.Count == 0)
            {
                MessageBox.Show(this,
                    "Could not locate 'String Table' resource! Some features may not work as expected. " +
                    "Was the file renamed or moved before initially opening it?\r\n\r\n" +
                    "- Make sure the '.msstyles' file is next to its multilingual resource files: '[lang-id]/[name].msstyles.mui'.\r\n" +
                    "- Make sure the names match.\r\n\r\n" +
                    "Then, reopen the file. After properly loading and saving the style, relocating the file is safe."
                    , "Warning, resource is missing!",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
        }

        private void CloseStyle()
        {
            m_sessionAddedClassIds.Clear();
            m_classView.SetVisualStyle(null);
            m_propertyView.SetStylePart(null, null, null);

            btFileSave.Enabled = false;
            btFileInfoExport.Enabled = false;
            btImageExport.Enabled = false;
            btImageImport.Enabled = false;
            btPropertyAdd.Enabled = false;
            btPropertyRemove.Enabled = false;
            btAddClass.Enabled = false;
            btAddPart.Enabled = false;
            btAddState.Enabled = false;
            btRemoveClass.Enabled = false;
            btRemovePart.Enabled = false;
            btRemoveState.Enabled = false;
            btTestTheme.Enabled = false;

            lbStylePlatform.Text = "";
            m_style?.Dispose();
        }

        private void DisplayClass(StyleClass cls)
        {
            // remember the shown class
            UpdateItemSelection(cls);
            // reset propery view
            m_propertyView.SetStylePart(m_style, cls, null);
            // reset image view
            m_selectedImage = UpdateImageView(null);
            // reset image selector
            m_imageView.SetActiveTabs(-1, 0);
        }

        private void ResetSelectedImageRegion()
        {
            m_selectedCropRect = null;
            m_selectedStateIndex = -1;
            m_selectedImageCount = 0;
            m_selectedImageLayout = 0;
        }

        private static bool TryGetAtlasRect(StyleState state, out Rectangle atlasRect)
        {
            atlasRect = Rectangle.Empty;
            if (state == null)
            {
                return false;
            }

            var rectProp = state.Properties.Find((p) => p.Header.nameID == (int)IDENTIFIER.ATLASRECT);
            if (rectProp == null)
            {
                return false;
            }

            var mt = rectProp.GetValueAs<Margins>();
            atlasRect = new Rectangle(
                mt.Left,
                mt.Top,
                Math.Max(mt.Right - mt.Left, 0),
                Math.Max(mt.Bottom - mt.Top, 0));

            return atlasRect.Width > 0 && atlasRect.Height > 0;
        }

        private static Rectangle SliceImageRegion(Rectangle imageRegion, int imageLayout, int imageCount, int imageIndex)
        {
            int left = imageRegion.Left;
            int top = imageRegion.Top;
            int right = imageRegion.Right;
            int bottom = imageRegion.Bottom;

            if (imageLayout == 1)
            {
                left += imageRegion.Width * imageIndex / imageCount;
                right = imageRegion.Left + imageRegion.Width * (imageIndex + 1) / imageCount;
            }
            else
            {
                top += imageRegion.Height * imageIndex / imageCount;
                bottom = imageRegion.Top + imageRegion.Height * (imageIndex + 1) / imageCount;
            }

            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        private bool TryGetSelectedAtlasRegion(StylePart part, StyleState state, out Rectangle atlasRegion)
        {
            atlasRegion = Rectangle.Empty;
            if (TryGetAtlasRect(state, out atlasRegion))
            {
                return true;
            }

            StyleState commonState;
            if (!part.States.TryGetValue(0, out commonState) || !TryGetAtlasRect(commonState, out var commonAtlasRegion))
            {
                return false;
            }

            if (state.StateId <= 0)
            {
                atlasRegion = commonAtlasRegion;
                return true;
            }

            int imageCount = 1;
            commonState.TryGetPropertyValue(IDENTIFIER.IMAGECOUNT, ref imageCount);
            if (imageCount > 1)
            {
                int imageLayout = 0;
                commonState.TryGetPropertyValue(IDENTIFIER.IMAGELAYOUT, ref imageLayout);

                int stateIndex = state.StateId - 1;
                if (stateIndex >= 0 && stateIndex < imageCount)
                {
                    atlasRegion = SliceImageRegion(commonAtlasRegion, imageLayout, imageCount, stateIndex);
                    return true;
                }
            }

            atlasRegion = commonAtlasRegion;
            return true;
        }

        private bool TryGetPartAtlasRegion(StylePart part, out Rectangle atlasRegion)
        {
            atlasRegion = Rectangle.Empty;

            StyleState commonState;
            if (part.States.TryGetValue(0, out commonState) && TryGetAtlasRect(commonState, out atlasRegion))
            {
                return true;
            }

            foreach (var state in part.States.Values)
            {
                if (TryGetAtlasRect(state, out atlasRegion))
                {
                    return true;
                }
            }

            return false;
        }

        private void DisplayPart(StyleClass cls, StylePart part)
        {
            // remember the shown class and part
            UpdateItemSelection(cls, part);
            // clear state-level crop
            ResetSelectedImageRegion();
            // reset propery view
            m_propertyView.SetStylePart(m_style, cls, part);

            // find ATLASRECT property so we can set the part highlights
            if (TryGetPartAtlasRegion(part, out var atlasRegion))
            {
                m_imageView.ViewHighlightArea = atlasRegion;
            }
            else m_imageView.ViewHighlightArea = null;

            // select the first image property of this part.
            // if the part doesn't have one, we take the first image of the "Common" part.
            StyleProperty imagePropToShow = null;
            var imgProps = part.GetImageProperties().ToList();
            if (imgProps.Count > 0)
            {
                imagePropToShow = imgProps[0];
            }
            else
            {
                StylePart commonPart;
                if(cls.Parts.TryGetValue(0, out commonPart))
                {
                    imagePropToShow = commonPart.GetImageProperties().FirstOrDefault();
                }
            }

            // reset image view
            m_selectedImage = UpdateImageView(imagePropToShow);
            // reset image selector
            m_imageView.SetActiveTabs(0, imgProps.Count);

            // TODO: ugly code..
            var renderer = new PartRenderer(m_style, part);
            m_renderView.Image = renderer.RenderPreview();
        }

        private void DisplayState(StyleClass cls, StylePart part, StyleState state)
        {
            // remember the shown class, part, and state
            UpdateItemSelection(cls, part, state);
            // show all properties for the parent part (same as clicking the part)
            m_propertyView.SetStylePart(m_style, cls, part);
            ResetSelectedImageRegion();

            if (TryGetSelectedAtlasRegion(part, state, out var atlasRegion))
            {
                m_selectedCropRect = atlasRegion;
            }
            else
            {
                // For non-atlas images, compute crop from IMAGECOUNT/IMAGELAYOUT
                // Properties like IMAGECOUNT are stored on state 0 (the "common" state)
                StyleState state0;
                if (state.StateId > 0 && part.States.TryGetValue(0, out state0))
                {
                    int imageCount = 1;
                    state0.TryGetPropertyValue(IDENTIFIER.IMAGECOUNT, ref imageCount);
                    if (imageCount > 1)
                    {
                        int imageLayout = 0; // 0=VERTICAL, 1=HORIZONTAL
                        state0.TryGetPropertyValue(IDENTIFIER.IMAGELAYOUT, ref imageLayout);
                        // State IDs are 1-based; index is stateId - 1
                        int stateIndex = state.StateId - 1;
                        if (stateIndex < imageCount)
                        {
                            // Defer actual rect calculation until we know image dimensions
                            // Store layout info for UpdateImageView
                            m_selectedStateIndex = stateIndex;
                            m_selectedImageCount = imageCount;
                            m_selectedImageLayout = imageLayout;
                        }
                    }
                }
            }

            // no highlight needed when cropping to just this state
            m_imageView.ViewHighlightArea = null;

            // find the image property: try the state first, then the part, then Common
            StyleProperty imgProp = state.Properties.Find((p) => p.IsImageProperty());
            if (imgProp == null)
            {
                imgProp = part.GetImageProperties().FirstOrDefault();
            }
            if (imgProp == null)
            {
                StylePart commonPart;
                if (cls.Parts.TryGetValue(0, out commonPart))
                {
                    imgProp = commonPart.GetImageProperties().FirstOrDefault();
                }
            }
            m_selectedImage = UpdateImageView(imgProp);

            // only one image tab for a single state
            m_imageView.SetActiveTabs(imgProp != null ? 0 : -1, imgProp != null ? 1 : 0);

            var renderer = new PartRenderer(m_style, part, state);
            m_renderView.Image = renderer.RenderPreview();
        }

        private StyleResource UpdateImageView(StyleProperty prop)
        {
            if (prop == null)
            {
                bool canAddImage = m_style != null &&
                                   m_style.Platform != Platform.WinXP &&
                                   m_selection != null &&
                                   m_selection.Class != null;

                btImageExport.Enabled = false;
                btImageImport.Enabled = canAddImage;
                btImageImport.Text = canAddImage ? "Add" : "Replace";
                btImageEdit.Enabled = false;
                btImageRecolor.Enabled = false;
                m_imageView.ViewImage = null;
                UpdateImageInfo(null);
                UpdateZoomInfo(null, m_imageView.ViewZoomFactor);
                return null;
            }

            // determine type for resource update
            StyleResourceType resType = StyleResourceType.None;
            if (prop.Header.typeID == (int)IDENTIFIER.FILENAME ||
               prop.Header.typeID == (int)IDENTIFIER.FILENAME_LITE)
            {
                resType = StyleResourceType.Image;
            }
            else if (prop.Header.typeID == (int)IDENTIFIER.DISKSTREAM)
            {
                resType = StyleResourceType.Atlas;
            }

            // see if there is a pending update to the resource
            string file = m_style.GetQueuedResourceUpdate(prop.Header.shortFlag, resType);

            // in any case, we have to store the update info of the real resource
            // we need that in order to export/replace?
            var resource = m_style.GetResourceFromProperty(prop);

            Image img = null;
            if (!String.IsNullOrEmpty(file))
            {
                img = Image.FromFile(file);
            }
            else
            {
                if (resource?.Data != null)
                {
                    img = Image.FromStream(new MemoryStream(resource.Data));
                }
            }

            // Compute crop rect from stacked layout if no ATLASRECT was found
            if (img != null && !m_selectedCropRect.HasValue && m_selectedStateIndex >= 0 && m_selectedImageCount > 1)
            {
                int partW, partH, offsetX, offsetY;
                if (m_selectedImageLayout == 1) // HORIZONTAL
                {
                    partW = img.Width / m_selectedImageCount;
                    partH = img.Height;
                    offsetX = partW * m_selectedStateIndex;
                    offsetY = 0;
                }
                else // VERTICAL (default)
                {
                    partW = img.Width;
                    partH = img.Height / m_selectedImageCount;
                    offsetX = 0;
                    offsetY = partH * m_selectedStateIndex;
                }
                m_selectedCropRect = new Rectangle(offsetX, offsetY, partW, partH);
            }

            // Crop to state region if a crop rect is set
            if (img != null && m_selectedCropRect.HasValue)
            {
                var crop = m_selectedCropRect.Value;
                // Clamp crop rect to actual image bounds
                crop.Intersect(new Rectangle(0, 0, img.Width, img.Height));
                if (crop.Width > 0 && crop.Height > 0)
                {
                    var cropped = new Bitmap(crop.Width, crop.Height);
                    using (var g = Graphics.FromImage(cropped))
                    {
                        g.DrawImage(img, 0, 0, crop, GraphicsUnit.Pixel);
                    }
                    img = cropped;
                }
            }

            bool xpReadOnly = m_style.Platform == Platform.WinXP;
            btImageExport.Enabled = resource.Data != null;
            btImageImport.Enabled = !xpReadOnly;
            btImageImport.Text = "Replace";
            btImageEdit.Enabled = !xpReadOnly && resource.Data != null;
            btImageRecolor.Enabled = !xpReadOnly && resource.Data != null;
            m_imageView.ViewImage = img;
            UpdateImageInfo(img);
            UpdateZoomInfo(img, m_imageView.ViewZoomFactor);
            return resource;
        }

        private Bitmap CropImage(Image source, Rectangle cropRect)
        {
            cropRect.Intersect(new Rectangle(0, 0, source.Width, source.Height));
            if (cropRect.Width <= 0 || cropRect.Height <= 0)
            {
                return new Bitmap(source);
            }
            var cropped = new Bitmap(cropRect.Width, cropRect.Height, source.PixelFormat);
            using (var g = Graphics.FromImage(cropped))
            {
                g.DrawImage(source, 0, 0, cropRect, GraphicsUnit.Pixel);
            }
            return cropped;
        }

        private Bitmap CompositeIntoAtlas(Image atlas, Image patch, Rectangle cropRect)
        {
            var result = new Bitmap(atlas);
            using (var g = Graphics.FromImage(result))
            {
                // Clear the target region first, then draw the patch
                g.SetClip(cropRect);
                g.Clear(Color.Transparent);
                g.ResetClip();
                g.DrawImage(patch, cropRect.X, cropRect.Y, cropRect.Width, cropRect.Height);
            }
            return result;
        }

        private Bitmap LoadResourceAsBitmap(StyleResource resource)
        {
            // Check if there's a pending update on disk (from a previous edit/import/recolor).
            // If so, load from that file instead of the original PE resource data.
            string queuedFile = m_style.GetQueuedResourceUpdate(resource.ResourceId, resource.Type);
            if (!String.IsNullOrEmpty(queuedFile) && File.Exists(queuedFile))
            {
                if (resource.Type == StyleResourceType.Image)
                {
                    // Queued IMAGE files are stored with premultiplied alpha, convert back
                    using (var fs = File.OpenRead(queuedFile))
                    {
                        Bitmap bmp;
                        libmsstyle.ImageConverter.PremulToStraightAlpha(fs, out bmp);
                        return bmp;
                    }
                }
                else
                {
                    return new Bitmap(queuedFile);
                }
            }

            // IMAGE resources are premultiplied; xp bitmaps are converted the same way at load.
            if (resource.Type == StyleResourceType.Image)
            {
                using (var ms = new MemoryStream(resource.Data))
                {
                    Bitmap bmp;
                    libmsstyle.ImageConverter.PremulToStraightAlpha(ms, out bmp);
                    return bmp;
                }
            }
            else
            {
                // Deep copy: GDI+ Bitmap keeps a reference to the source stream,
                // so we must copy the pixels to a new Bitmap before disposing it.
                using (var ms = new MemoryStream(resource.Data))
                using (var temp = new Bitmap(ms))
                {
                    return new Bitmap(temp);
                }
            }
        }

        private void SaveResourceBitmap(Bitmap bmp, StyleResourceType type, string outputPath)
        {
            if (type == StyleResourceType.Image)
            {
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    ms.Position = 0;
                    Bitmap premul;
                    libmsstyle.ImageConverter.PremultiplyAlpha(ms, out premul);
                    premul.Save(outputPath, ImageFormat.Png);
                }
            }
            else
            {
                bmp.Save(outputPath, ImageFormat.Png);
            }
        }

        private void RefreshCurrentImageView()
        {
            if (m_selection.Part == null)
            {
                return;
            }

            StyleProperty imgProp;
            if (m_selection.State != null)
            {
                // State node selected — try state, then part, then Common
                imgProp = m_selection.State.Properties.Find(p => p.IsImageProperty());
                if (imgProp == null)
                {
                    imgProp = m_selection.Part.GetImageProperties().FirstOrDefault();
                }
                if (imgProp == null)
                {
                    StylePart commonPart;
                    if (m_selection.Class.Parts.TryGetValue(0, out commonPart))
                    {
                        imgProp = commonPart.GetImageProperties().FirstOrDefault();
                    }
                }
            }
            else
            {
                // Part node selected — use the tab index across all states
                imgProp = m_selection.Part.GetImageProperties().ElementAtOrDefault(m_imageView.SelectedIndex);
            }

            if (imgProp != null)
            {
                m_selectedImage = UpdateImageView(imgProp);
            }

            var renderer = new PartRenderer(m_style, m_selection.Part, m_selection.State);
            m_renderView.Image = renderer.RenderPreview();
        }

        private int GetNextImageResourceId()
        {
            int maxResourceId = 0;

            foreach (var cls in m_style.Classes)
            {
                foreach (var part in cls.Value.Parts)
                {
                    foreach (var state in part.Value.States)
                    {
                        foreach (var prop in state.Value.Properties)
                        {
                            if (prop.IsImageProperty() && prop.Header.shortFlag > maxResourceId)
                            {
                                maxResourceId = prop.Header.shortFlag;
                            }
                        }
                    }
                }
            }

            return Math.Max(1, maxResourceId + 1);
        }

        private int GetNextImagePropertyNameId(StyleState state)
        {
            var candidates = new int[]
            {
                (int)IDENTIFIER.IMAGEFILE,
                (int)IDENTIFIER.IMAGEFILE1,
                (int)IDENTIFIER.IMAGEFILE2,
                (int)IDENTIFIER.IMAGEFILE3,
                (int)IDENTIFIER.IMAGEFILE4,
                (int)IDENTIFIER.IMAGEFILE5,
                (int)IDENTIFIER.IMAGEFILE6,
                (int)IDENTIFIER.IMAGEFILE7,
                (int)IDENTIFIER.GLYPHIMAGEFILE,
            };

            var used = new HashSet<int>(state.Properties.Select(p => p.Header.nameID));
            return candidates.FirstOrDefault(id => !used.Contains(id));
        }

        private bool TryCreateImagePropertyForCurrentSelection(
            out StyleClass targetClass,
            out StylePart targetPart,
            out StyleState targetState,
            out StyleProperty newImageProp,
            out string error)
        {
            targetClass = null;
            targetPart = null;
            targetState = null;
            newImageProp = null;
            error = null;

            if (m_style == null)
            {
                error = "Open a style first.";
                return false;
            }

            if (m_style.Platform == Platform.WinXP)
            {
                error = "Adding new image resources is currently only supported for Vista+ styles.";
                return false;
            }

            targetClass = m_selection?.Class;
            if (targetClass == null)
            {
                error = "Select a class, part, or state first.";
                return false;
            }

            // Class selection: use class common part (ID 0).
            // Part selection: use that part's common state (ID 0).
            // State selection: use that exact state.
            targetPart = m_selection.Part;
            if (targetPart == null)
            {
                if (!targetClass.Parts.TryGetValue(0, out targetPart))
                {
                    targetPart = new StylePart
                    {
                        PartId = 0,
                        PartName = "Common Properties"
                    };
                    targetClass.Parts[0] = targetPart;
                }
            }

            targetState = m_selection.State;
            if (targetState == null || !targetPart.States.ContainsKey(targetState.StateId))
            {
                if (!targetPart.States.TryGetValue(0, out targetState))
                {
                    targetState = new StyleState
                    {
                        StateId = 0,
                        StateName = "Common"
                    };
                    targetPart.States[0] = targetState;
                }
            }

            int nameId = GetNextImagePropertyNameId(targetState);
            if (nameId == 0)
            {
                error = "No free image property slot is available in this state (IMAGEFILE..IMAGEFILE7 / GLYPHIMAGEFILE).";
                return false;
            }

            int newResourceId = GetNextImageResourceId();
            newImageProp = new StyleProperty((IDENTIFIER)nameId, IDENTIFIER.FILENAME);
            newImageProp.Header.classID = targetClass.ClassId;
            newImageProp.Header.partID = targetPart.PartId;
            newImageProp.Header.stateID = targetState.StateId;
            newImageProp.SetValue(newResourceId);
            targetState.Properties.Add(newImageProp);

            return true;
        }

        #endregion

        #region Functionality

        private void OnFileOpenClick(object sender, EventArgs e)
        {
            var ofd = new OpenFileDialog()
            {
                Title = "Open Visual Style",
                Filter = "Visual Style (*.msstyles)|*.msstyles|All Files (*.*)|*.*"
            };

            if (ofd.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            CloseStyle();
            OpenStyle(ofd.FileName);
        }

        private void OnFileSaveClick(object sender, EventArgs e)
        {
            var sfd = new SaveFileDialog()
            {
                Title = "Save Visual Style",
                Filter = "Visual Style (*.msstyles)|*.msstyles",
                OverwritePrompt = true
            };

            if (sfd.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            try
            {
                if (sender == btFileSave)
                    m_style.Save(sfd.FileName, true);
                else if (sender == btFileSaveWithMUI)
                    m_style.Save(sfd.FileName, false);

                lbStatusMessage.Text = "Style saved successfully!";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error saving file!", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0)
            {
                return;
            }

            var ext = Path.GetExtension(files[0]).ToLower();
            if (ext == ".msstyles")
            {
                e.Effect = DragDropEffects.Copy;
            }
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0)
            {
                return;
            }

            CloseStyle();
            OpenStyle(files[0]);
        }

        private void OnExportStyleInfo(object sender, EventArgs e)
        {
            var sfd = new SaveFileDialog()
            {
                Title = "Export Style Info",
                Filter = "Style Info (*.txt)|*.txt"
            };

            if (sfd.ShowDialog() == DialogResult.OK)
            {
                Exporter.ExportLogicalStructure(sfd.FileName, m_style);
            }
        }

        private void OnOpenThemeFolder(object sender, EventArgs e)
        {
            try
            {
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var args = String.Format("{0}\\Resources\\Themes\\", folder);
                Process.Start("explorer", args);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error opening theme folder!", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void OnImportWin7Style(object sender, EventArgs e)
        {
            if (m_style == null || (m_style.Platform != Platform.Win10 && m_style.Platform != Platform.Win11))
            {
                MessageBox.Show(this, "Open the Windows 10 style to import into first (e.g. an Aero 7 port that already works on your system).",
                    "Import Windows 7 style", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string source;
            using (var ofd = new OpenFileDialog { Title = "Windows 7 style to import", Filter = "Visual Style (*.msstyles)|*.msstyles" })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                source = ofd.FileName;
            }

            // the open style may have unsaved edits, so the template is a snapshot of it, not its file.
            string work = Path.Combine(Path.GetTempPath(), "msstyleEditor-import", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            string name = Path.GetFileNameWithoutExtension(m_style.Path);
            string snapshot = Path.Combine(work, "template", name + ".msstyles");
            string candidate = Path.Combine(work, "out", name + "-win7import.msstyles");
            // base stays empty: the importer uses the stock win7 aero compiled into libmsstyle.
            var opt = new libmsstyle.Import.Win7ImportOptions
            {
                SourcePath = source,
                TemplatePath = snapshot,
                OutputPath = candidate,
                // the snapshot sits alone in temp; Shell and the .mui come from the file that's open.
                CompanionSourcePath = m_style.Path,
                // the editor's import always keeps Windows 10's left task pane; the xp band is the CLI's --task-band.
                TaskBand = false,
                DryRun = false,
            };

            libmsstyle.Import.ImportReport report;
            List<string> reportFiles;
            Cursor = Cursors.WaitCursor;
            btImportWin7.Enabled = false;
            UpdateStatusText("Importing " + Path.GetFileName(source) + "...");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(snapshot));
                m_style.Save(snapshot, true);
                report = await System.Threading.Tasks.Task.Run(() => libmsstyle.Import.Win7StyleImporter.Run(opt));
                reportFiles = libmsstyle.Import.ImportReportWriter.Write(report, Path.Combine(work, "out", "import-report"));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Import failed!", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusText("Import failed.");
                return;
            }
            finally
            {
                Cursor = Cursors.Default;
                btImportWin7.Enabled = true;
            }

            bool ok = report.Status == libmsstyle.Import.ImportStatus.ReadyForVmTest;

            using (var dlg = new Win7ImportDialog(report, reportFiles[1]))
            {
                dlg.ShowDialog(this);
            }

            if (ok)
            {
                CloseStyle();
                OpenStyle(candidate);
                UpdateStatusText("Imported " + Path.GetFileName(source) + ". Not saved yet: test it, keep editing, then save to a new file.");
            }
            else
            {
                UpdateStatusText("Import blocked, the open style is unchanged. See the report.");
            }
        }

        private async void OnImportXpTheme(object sender, EventArgs e)
        {
            if (m_style == null || (m_style.Platform != Platform.Win10 && m_style.Platform != Platform.Win11))
            {
                MessageBox.Show(this, "Open the Windows 10 XP port to build on first (e.g. a Luna port that already works on your system).",
                    "Import Windows XP Style", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string source;
            using (var ofd = new OpenFileDialog { Title = "Windows XP style to import", Filter = "Visual Style (*.msstyles)|*.msstyles" })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                source = ofd.FileName;
            }

            string outDir;
            using (var fbd = new FolderBrowserDialog { Description = "Folder for the ported theme (one style folder and .theme per color scheme)" })
            {
                if (fbd.ShowDialog(this) != DialogResult.OK) return;
                outDir = fbd.SelectedPath;
            }

            // the open style may have unsaved edits, so the template is a snapshot of it, not its file.
            string work = Path.Combine(Path.GetTempPath(), "msstyleEditor-import", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            string snapshot = Path.Combine(work, "template", Path.GetFileName(m_style.Path));
            var opt = new libmsstyle.Import.XpThemeImportOptions
            {
                SourcePath = source,
                TemplatePath = snapshot,
                // the snapshot sits alone in temp; Shell and the .mui come from the file that's open.
                CompanionSourcePath = m_style.Path,
                OutDir = outDir,
                // the editor's import always keeps Windows 10's left task pane; the xp band is the CLI's --task-band.
                TaskBand = false,
                Write = true,
            };

            List<libmsstyle.Import.XpSchemeResult> results;
            Cursor = Cursors.WaitCursor;
            btImportXp.Enabled = false;
            UpdateStatusText("Importing " + Path.GetFileName(source) + "...");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(snapshot));
                m_style.Save(snapshot, true);
                results = await System.Threading.Tasks.Task.Run(() => libmsstyle.Import.XpThemeImport.Run(opt,
                    (scheme, file) => BeginInvoke((Action)(() => UpdateStatusText($"Importing {Path.GetFileName(source)}: {scheme.DisplayName.Trim()}...")))));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Import failed!", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusText("Import failed.");
                return;
            }
            finally
            {
                Cursor = Cursors.Default;
                btImportXp.Enabled = true;
            }

            string open = null;
            using (var dlg = new XpImportDialog(results, outDir))
            {
                dlg.ShowDialog(this);
                open = dlg.StyleToOpen;
            }

            int ok = results.Count(r => r.Ok);
            if (open != null)
            {
                CloseStyle();
                OpenStyle(open);
            }
            UpdateStatusText($"XP import: {ok} of {results.Count} color schemes ported to {outDir}.");
        }

        private void OnControlPreview(object sender, EventArgs e)
        {
            var dlg = new ControlDemoDialog();
            dlg.Show();
        }

        private void OnTreeItemSelected(object sender, TreeViewEventArgs e)
        {
            if (e.Node == null)
            {
                return;
            }

            StyleClass cls = e.Node.Tag as StyleClass;
            if (cls != null)
            {
                DisplayClass(cls);
                return;
            }

            StylePart part = e.Node.Tag as StylePart;
            if (part != null)
            {
                cls = e.Node.Parent.Tag as StyleClass;
                Debug.Assert(cls != null);

                DisplayPart(cls, part);
                return;
            }

            StyleState state = e.Node.Tag as StyleState;
            if (state != null)
            {
                part = e.Node.Parent.Tag as StylePart;
                cls = e.Node.Parent.Parent.Tag as StyleClass;
                Debug.Assert(part != null && cls != null);

                DisplayState(cls, part, state);
                return;
            }

            TimingFunction func = e.Node.Tag as TimingFunction;
            if (func != null)
            {
                m_selectedTimingFunction = func;
                m_propertyView.SetTimingFunction(func);
                return;
            }

            AnimationTypeDescriptor animation = e.Node.Tag as AnimationTypeDescriptor;
            if (animation != null)
            {
                m_selectedAnimation = animation;
                m_propertyView.SetAnimation(animation);
                return;
            }

            //nothing valid is selected, clear the property grid
            m_propertyView.SetStylePart(null, null, null);
        }

        private void OnTreeExpandClick(object sender, EventArgs e)
        {
            m_classView.ExpandAll();
        }

        private void OnTreeCollapseClick(object sender, EventArgs e)
        {
            m_classView.CollapseAll();
        }

        private void OnToggleRenderView(object sender, EventArgs e)
        {
            m_renderView.IsHidden = !m_renderView.IsHidden;
        }

        private void OnToggleImageView(object sender, EventArgs e)
        {
            m_imageView.IsHidden = !m_imageView.IsHidden;
        }

        private void OnTestTheme(object sender, EventArgs e)
        {
            if (m_themeManager.IsThemeInUse)
            {
                try
                {
                    System.Threading.Thread.Sleep(250); // prevent doubleclicks
                    m_themeManager.Rollback();
                    SetTestThemeButtonState(m_themeManager.IsThemeInUse);
                    return;
                }
                catch (Exception) { }
            }

            Win32Api.OSVERSIONINFOEXW version = new Win32Api.OSVERSIONINFOEXW()
            {
                dwOSVersionInfoSize = Marshal.SizeOf(typeof(Win32Api.OSVERSIONINFOEXW))
            };
            Win32Api.RtlGetVersion(ref version);

            bool needConfirmation = false;
            if (version.dwMajorVersion == 6 &&
                version.dwMinorVersion == 0 &&
                m_style.Platform != Platform.Vista)
            {
                needConfirmation = true;
            }

            if (version.dwMajorVersion == 6 &&
                version.dwMinorVersion == 1 &&
                m_style.Platform != Platform.Win7)
            {
                needConfirmation = true;
            }

            if (version.dwMajorVersion == 6 &&
                (version.dwMinorVersion == 2 || version.dwMinorVersion == 3) &&
                m_style.Platform != Platform.Win8 &&
                m_style.Platform != Platform.Win81)
            {
                needConfirmation = true;
            }

            if (version.dwMajorVersion == 10 &&
                version.dwMinorVersion == 0 &&
                version.dwBuildNumber < 22000 &&
                m_style.Platform != Platform.Win10)
            {
                needConfirmation = true;
            }

            if (version.dwMajorVersion == 10 &&
                version.dwMinorVersion == 0 &&
                version.dwBuildNumber >= 22000 &&
                m_style.Platform != Platform.Win11)
            {
                needConfirmation = true;
            }

            if (needConfirmation)
            {
                if (MessageBox.Show("It looks like the style was not made for this windows version. Try to apply it anyways?"
                    , "msstyleEditor"
                    , MessageBoxButtons.YesNo
                    , MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }
            }

            try
            {
                System.Threading.Thread.Sleep(250); // prevent doubleclicks
                m_themeManager.ApplyTheme(m_style);
                SetTestThemeButtonState(m_themeManager.IsThemeInUse);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "msstyleEditor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnReloadTest(object sender, EventArgs e)
        {
            if (!m_themeManager.IsThemeInUse)
                return;

            try
            {
                System.Threading.Thread.Sleep(250);
                m_themeManager.Rollback();
                m_themeManager.ApplyTheme(m_style);
                SetTestThemeButtonState(m_themeManager.IsThemeInUse);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "msstyleEditor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnImageViewBackgroundChange(object sender, EventArgs e)
        {
            // we want to change the color
            if (sender == btImageBgWhite)
            {
                m_imageView.ViewBackColor = Color.White;
                btImageBgWhite.Checked = true;
            }
            else if (sender == btImageBgGrey)
            {
                m_imageView.ViewBackColor = Color.LightGray;
                btImageBgGrey.Checked = true;
            }
            else if (sender == btImageBgBlack)
            {
                m_imageView.ViewBackColor = Color.Black;
                btImageBgBlack.Checked = true;
            }
            else if (sender == btImageBgChecker)
            {
                m_imageView.ViewBackColor = Color.MediumVioletRed;
                btImageBgChecker.Checked = true;
            }
        }

        private void OnImageViewBackgroundChanged(object sender, EventArgs e)
        {
            // we get notified because someone else changed the color
            if (m_imageView.ViewBackColor == Color.White) btImageBgWhite.Checked = true;
            if (m_imageView.ViewBackColor == Color.LightGray) btImageBgGrey.Checked = true;
            if (m_imageView.ViewBackColor == Color.Black) btImageBgBlack.Checked = true;
            if (m_imageView.ViewBackColor == Color.MediumVioletRed) btImageBgChecker.Checked = true; // hack
        }

        private void OnImageViewZoomChanged(object sender, EventArgs e)
        {
            UpdateZoomInfo(m_imageView.ViewImage, m_imageView.ViewZoomFactor);
        }

        private void OnImageExport(object sender, EventArgs e)
        {
            if (m_selectedImage == null)
            {
                MessageBox.Show("Select an image first!", "Export Image", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (m_selectedImage.Data == null)
            {
                MessageBox.Show("This image resource doesn't exist yet!", "Export Image", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using (var sfd = new SaveFileDialog())
            {
                string suggestedName;
                if (m_selection.State != null)
                {
                    suggestedName = String.Format("{0}_{1}_{2}_{3}.png",
                        m_selection.Class.ClassName,
                        m_selection.Part.PartName,
                        m_selection.State.StateName,
                        m_selectedImage.ResourceId.ToString());
                }
                else
                {
                    suggestedName = String.Format("{0}_{1}_{2}.png",
                        m_selection.Class.ClassName,
                        m_selection.Part.PartName,
                        m_selectedImage.ResourceId.ToString());
                }

                foreach (var c in Path.GetInvalidFileNameChars())
                {
                    suggestedName = suggestedName.Replace(c, '-');
                }

                sfd.Title = "Export Image";
                sfd.Filter = "PNG Image (*.png)|*.png";
                sfd.FileName = suggestedName;
                if (sfd.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    using (var bmp = LoadResourceAsBitmap(m_selectedImage))
                    {
                        if (m_selectedCropRect.HasValue)
                        {
                            using (var cropped = CropImage(bmp, m_selectedCropRect.Value))
                            {
                                cropped.Save(sfd.FileName, ImageFormat.Png);
                            }
                        }
                        else
                        {
                            bmp.Save(sfd.FileName, ImageFormat.Png);
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Error saving image!", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                UpdateStatusText("Image exported successfully!");
            }
        }

        private void OnImageImport(object sender, EventArgs e)
        {
            bool addingNewImage = false;
            StyleResource targetImage = m_selectedImage;
            StyleClass createdClass = null;
            StylePart createdPart = null;
            StyleState createdState = null;
            StyleProperty createdImageProp = null;

            if (targetImage == null)
            {
                string error;
                if (!TryCreateImagePropertyForCurrentSelection(out createdClass, out createdPart, out createdState, out createdImageProp, out error))
                {
                    MessageBox.Show(error, "Add Image", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                addingNewImage = true;

                // Ensure the class tree reflects newly created default part/state when needed.
                m_classView.SetVisualStyle(m_style);

                // Switch the UI to the target part and preselect the new image slot.
                DisplayPart(createdClass, createdPart);
                var imageProps = createdPart.GetImageProperties().ToList();
                int imageIndex = imageProps.IndexOf(createdImageProp);
                m_imageView.SetActiveTabs(imageIndex >= 0 ? imageIndex : 0, imageProps.Count);

                targetImage = UpdateImageView(createdImageProp);
                m_selectedImage = targetImage;
                m_selectedCropRect = null;
            }

            if (targetImage == null)
            {
                MessageBox.Show("Could not resolve a target image resource.",
                    "Add Image", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = addingNewImage ? "Add Image" : "Replace Image";
                ofd.Filter = "PNG Image (*.png)|*.png";
                if (ofd.ShowDialog() != DialogResult.OK)
                {
                    if (addingNewImage && createdState != null && createdImageProp != null)
                    {
                        createdState.Properties.Remove(createdImageProp);
                        DisplayPart(createdClass, createdPart);
                    }
                    return;
                }

                var res = ImageFormats.IsImageSupported(ofd.FileName);
                if (!res.Item1)
                {
                    if (addingNewImage && createdState != null && createdImageProp != null)
                    {
                        createdState.Properties.Remove(createdImageProp);
                        DisplayPart(createdClass, createdPart);
                    }

                    MessageBox.Show("Bad image:\n" + res.Item2,
                        addingNewImage ? "Add Image" : "Replace Image",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                var fname = Path.GetRandomFileName() + Path.GetExtension(ofd.FileName);
                var tempFolder = Path.Combine(Path.GetTempPath(), "msstyleEditor");
                Directory.CreateDirectory(tempFolder);
                var tempFile = Path.Combine(tempFolder, fname);

                if (m_selectedCropRect.HasValue && !addingNewImage)
                {
                    // State-level import: composite the imported image into the atlas.
                    using (var patch = new Bitmap(ofd.FileName))
                    using (var atlas = LoadResourceAsBitmap(targetImage))
                    using (var composited = CompositeIntoAtlas(atlas, patch, m_selectedCropRect.Value))
                    {
                        SaveResourceBitmap(composited, targetImage.Type, tempFile);
                    }
                }
                else if (targetImage.Type == StyleResourceType.Image)
                {
                    // IMAGE resources must be saved with premultiplied alpha channel.
                    using (var ifs = File.OpenRead(ofd.FileName))
                    using (var ofs = File.Create(tempFile))
                    {
                        Bitmap bmp;
                        libmsstyle.ImageConverter.PremultiplyAlpha(ifs, out bmp);
                        bmp.Save(ofs, ImageFormat.Png);
                    }
                }
                else
                {
                    // STREAM resources must be saved with straight alpha channel.
                    File.Copy(ofd.FileName, tempFile, true);
                }

                m_style.QueueResourceUpdate(targetImage.ResourceId, targetImage.Type, tempFile);
                RefreshCurrentImageView();
                UpdateStatusText(addingNewImage ? "Image added successfully!" : "Image replaced successfully!");
            }
        }

        private void OnSetImageEditor(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "Select Image Editor";
                ofd.Filter = "Executable (*.exe)|*.exe";

                var settings = new RegistrySettings();
                string current = settings.ImageEditorPath;
                if (!String.IsNullOrEmpty(current) && Directory.Exists(Path.GetDirectoryName(current)))
                {
                    ofd.InitialDirectory = Path.GetDirectoryName(current);
                }

                if (ofd.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                settings.ImageEditorPath = ofd.FileName;
                UpdateStatusText("Image editor set to: " + ofd.FileName);
            }
        }

        private void OnImageEdit(object sender, EventArgs e)
        {
            if (m_selectedImage == null)
            {
                MessageBox.Show("Select an image first!", "Edit Image", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (m_selectedImage.Data == null)
            {
                MessageBox.Show("This image resource doesn't exist yet!", "Edit Image", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string displayName;
            if (m_selection.State != null)
            {
                displayName = String.Format("{0}_{1}_{2}_{3}",
                    m_selection.Class.ClassName,
                    m_selection.Part.PartName,
                    m_selection.State.StateName,
                    m_selectedImage.ResourceId.ToString());
            }
            else
            {
                displayName = String.Format("{0}_{1}_{2}",
                    m_selection.Class.ClassName,
                    m_selection.Part.PartName,
                    m_selectedImage.ResourceId.ToString());
            }

            EditImageResource(m_selectedImage, displayName, m_selectedCropRect);
        }

        private void OnImageRecolor(object sender, EventArgs e)
        {
            if (m_selectedImage == null || m_selectedImage.Data == null)
            {
                MessageBox.Show("Select an image first!", "Recolor Image", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var colorDialog = new ColorDialog())
            {
                colorDialog.FullOpen = true;
                if (colorDialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                var targetColor = colorDialog.Color;
                // Convert target color to HSV to get target hue
                double targetHue = RgbToHsv(targetColor.R / 255.0, targetColor.G / 255.0, targetColor.B / 255.0).h;

                try
                {
                    using (var fullBmp = LoadResourceAsBitmap(m_selectedImage))
                    {
                        Bitmap bmpToRecolor;
                        if (m_selectedCropRect.HasValue)
                        {
                            bmpToRecolor = CropImage(fullBmp, m_selectedCropRect.Value);
                        }
                        else
                        {
                            bmpToRecolor = new Bitmap(fullBmp);
                        }

                        RecolorBitmap(bmpToRecolor, targetHue);

                        Bitmap result;
                        if (m_selectedCropRect.HasValue)
                        {
                            result = CompositeIntoAtlas(fullBmp, bmpToRecolor, m_selectedCropRect.Value);
                        }
                        else
                        {
                            result = bmpToRecolor;
                        }

                        var tempFolder = Path.Combine(Path.GetTempPath(), "msstyleEditor");
                        Directory.CreateDirectory(tempFolder);
                        var tempFile = Path.Combine(tempFolder, Path.GetRandomFileName() + ".png");

                        SaveResourceBitmap(result, m_selectedImage.Type, tempFile);
                        m_style.QueueResourceUpdate(m_selectedImage.ResourceId, m_selectedImage.Type, tempFile);
                        RefreshCurrentImageView();
                        UpdateStatusText("Image recolored successfully!");

                        if (m_selectedCropRect.HasValue)
                        {
                            result.Dispose();
                        }
                        bmpToRecolor.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Error recoloring image!", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private struct HsvColor
        {
            public double h, s, v;
        }

        private static HsvColor RgbToHsv(double r, double g, double b)
        {
            HsvColor result;
            double min = Math.Min(r, Math.Min(g, b));
            double max = Math.Max(r, Math.Max(g, b));
            result.v = max;
            double delta = max - min;
            if (delta < 0.00001)
            {
                result.s = 0;
                result.h = 0;
                return result;
            }
            if (max > 0.0)
            {
                result.s = delta / max;
            }
            else
            {
                result.s = 0;
                result.h = 0;
                return result;
            }
            if (r >= max)
                result.h = (g - b) / delta;
            else if (g >= max)
                result.h = 2.0 + (b - r) / delta;
            else
                result.h = 4.0 + (r - g) / delta;
            result.h *= 60.0;
            if (result.h < 0.0)
                result.h += 360.0;
            return result;
        }

        private static Color HsvToRgb(double h, double s, double v, int alpha)
        {
            if (s <= 0.0)
                return Color.FromArgb(alpha, (int)(v * 255), (int)(v * 255), (int)(v * 255));

            double hh = h >= 360.0 ? 0.0 : h;
            hh /= 60.0;
            int i = (int)hh;
            double ff = hh - i;
            double p = v * (1.0 - s);
            double q = v * (1.0 - (s * ff));
            double t = v * (1.0 - (s * (1.0 - ff)));

            double r, g, b;
            switch (i)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }

            return Color.FromArgb(alpha,
                Math.Min(255, Math.Max(0, (int)(r * 255))),
                Math.Min(255, Math.Max(0, (int)(g * 255))),
                Math.Min(255, Math.Max(0, (int)(b * 255))));
        }

        private static void RecolorBitmap(Bitmap bmp, double targetHue)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var bmpData = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            int byteCount = Math.Abs(bmpData.Stride) * bmpData.Height;
            byte[] pixels = new byte[byteCount];
            System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, pixels, 0, byteCount);

            for (int i = 0; i < byteCount; i += 4)
            {
                int b = pixels[i];
                int g = pixels[i + 1];
                int r = pixels[i + 2];
                int a = pixels[i + 3];

                if (a == 0) continue;

                var hsv = RgbToHsv(r / 255.0, g / 255.0, b / 255.0);
                hsv.h = targetHue;
                var newColor = HsvToRgb(hsv.h, hsv.s, hsv.v, a);

                pixels[i] = newColor.B;
                pixels[i + 1] = newColor.G;
                pixels[i + 2] = newColor.R;
                pixels[i + 3] = (byte)a;
            }

            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmpData.Scan0, byteCount);
            bmp.UnlockBits(bmpData);
        }

        private void EditImageResource(StyleResource resource, string displayName, Rectangle? cropRect)
        {
            var settings = new RegistrySettings();
            string editorPath = settings.ImageEditorPath;
            if (String.IsNullOrEmpty(editorPath) || !File.Exists(editorPath))
            {
                var result = MessageBox.Show("No image editor configured. Would you like to set one now?",
                    "Edit Image", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result == DialogResult.Yes)
                {
                    OnSetImageEditor(this, EventArgs.Empty);
                    editorPath = settings.ImageEditorPath;
                    if (String.IsNullOrEmpty(editorPath) || !File.Exists(editorPath))
                    {
                        return;
                    }
                }
                else
                {
                    return;
                }
            }

            // Export the current image to a temp file for editing
            var tempFolder = Path.Combine(Path.GetTempPath(), "msstyleEditor");
            Directory.CreateDirectory(tempFolder);

            string suggestedName = displayName + ".png";
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                suggestedName = suggestedName.Replace(c, '-');
            }

            var tempFile = Path.Combine(tempFolder, suggestedName);

            try
            {
                using (var bmp = LoadResourceAsBitmap(resource))
                {
                    if (cropRect.HasValue)
                    {
                        using (var cropped = CropImage(bmp, cropRect.Value))
                        {
                            cropped.Save(tempFile);
                        }
                    }
                    else
                    {
                        bmp.Save(tempFile);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error exporting image for editing!", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Launch the external editor
            try
            {
                Process.Start(editorPath, "\"" + tempFile + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error launching image editor!", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Show OK/Cancel dialog for the user to confirm when done editing
            var editResult = MessageBox.Show(
                "Edit the image in the external editor and save it.\n\n" +
                "Click OK when you're done to apply changes, or Cancel to discard.",
                "Edit Image", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);

            if (editResult == DialogResult.OK)
            {
                // Validate the edited image
                var validation = ImageFormats.IsImageSupported(tempFile);
                if (!validation.Item1)
                {
                    MessageBox.Show("Bad image:\n" + validation.Item2, "Edit Image", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Convert and queue the update
                var processedFile = Path.Combine(tempFolder, Path.GetRandomFileName() + ".png");
                try
                {
                    if (cropRect.HasValue)
                    {
                        // Composite the edited crop back into the full atlas
                        using (var patch = new Bitmap(tempFile))
                        using (var atlas = LoadResourceAsBitmap(resource))
                        using (var composited = CompositeIntoAtlas(atlas, patch, cropRect.Value))
                        {
                            SaveResourceBitmap(composited, resource.Type, processedFile);
                        }
                    }
                    else if (resource.Type == StyleResourceType.Image)
                    {
                        using (var ifs = File.OpenRead(tempFile))
                        using (var ofs = File.Create(processedFile))
                        {
                            Bitmap bmp;
                            libmsstyle.ImageConverter.PremultiplyAlpha(ifs, out bmp);
                            bmp.Save(ofs, ImageFormat.Png);
                        }
                    }
                    else
                    {
                        File.Copy(tempFile, processedFile, true);
                    }

                    m_style.QueueResourceUpdate(resource.ResourceId, resource.Type, processedFile);
                    RefreshCurrentImageView();
                    UpdateStatusText("Image updated successfully!");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Error applying edited image!", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            // Clean up the editing temp file
            try { File.Delete(tempFile); } catch { }
        }

        private void OnPropertyAdd(object sender, EventArgs e)
        {
            m_propertyView.ShowPropertyAddDialog();
        }

        private void OnPropertyAdded(StyleProperty prop)
        {
            // refresh gui to account for new image property
            if (prop.IsImageProperty())
            {
                DisplayPart(m_selection.Class, m_selection.Part);
            }
        }

        private void OnPropertyRemove(object sender, EventArgs e)
        {
            m_propertyView.RemoveSelectedProperty();
        }

        private void OnPropertyRemoved(StyleProperty prop)
        {
            // refresh gui to account for removed image property
            if (prop.IsImageProperty())
            {
                DisplayPart(m_selection.Class, m_selection.Part);
            }
        }

        private void OnAddClass(object sender, EventArgs e)
        {
            if (m_style == null)
                return;

            string className;
            int baseClassId;
            if (!ShowAddClassDialog(out className, out baseClassId))
                return;

            // Get the next available class ID
            int newClassId = m_style.Classes.Count > 0 ? m_style.Classes.Keys.Max() + 1 : 0;

            var newClass = new StyleClass
            {
                ClassId = newClassId,
                ClassName = className.Trim(),
                BaseClassId = baseClassId
            };

            // Add "Common Properties" part (part 0) with default state 0 = "Common"
            var commonPart = new StylePart
            {
                PartId = 0,
                PartName = "Common Properties"
            };
            var commonState = new StyleState
            {
                StateId = 0,
                StateName = "Common"
            };

            commonPart.States[0] = commonState;
            newClass.Parts[0] = commonPart;

            m_style.Classes[newClassId] = newClass;
            m_style.MarkClassmapDirty();
            m_sessionAddedClassIds.Add(newClassId);
            m_classView.AddClassNode(newClass);
        }


        private bool TryGetSelectedClassNode(out TreeNode classNode, out StyleClass cls)
        {
            classNode = null;
            cls = null;

            var selectedNode = m_classView.SelectedNode;
            if (selectedNode == null)
            {
                return false;
            }

            cls = selectedNode.Tag as StyleClass;
            if (cls != null)
            {
                classNode = selectedNode;
                return true;
            }

            if (selectedNode.Tag is StylePart)
            {
                classNode = selectedNode.Parent;
                cls = classNode?.Tag as StyleClass;
                return cls != null;
            }

            if (selectedNode.Tag is StyleState)
            {
                classNode = selectedNode.Parent?.Parent;
                cls = classNode?.Tag as StyleClass;
                return cls != null;
            }

            return false;
        }

        private bool TryGetSelectedPartNode(out TreeNode partNode, out StyleClass cls, out StylePart part)
        {
            partNode = null;
            cls = null;
            part = null;

            var selectedNode = m_classView.SelectedNode;
            if (selectedNode == null)
            {
                return false;
            }

            part = selectedNode.Tag as StylePart;
            if (part != null)
            {
                partNode = selectedNode;
                cls = selectedNode.Parent?.Tag as StyleClass;
                return cls != null;
            }

            if (selectedNode.Tag is StyleState)
            {
                partNode = selectedNode.Parent;
                part = partNode?.Tag as StylePart;
                cls = partNode?.Parent?.Tag as StyleClass;
                return part != null && cls != null;
            }

            return false;
        }

        private void OnAddPart(object sender, EventArgs e)
        {
            if (m_style == null)
                return;

            TreeNode classNode;
            StyleClass cls;
            if (!TryGetSelectedClassNode(out classNode, out cls))
            {
                MessageBox.Show("Please select a class first.", "Add Part",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (cls.ClassName.Equals("animations", StringComparison.OrdinalIgnoreCase) ||
                cls.ClassName.Equals("timingfunction", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Adding parts to this class is not supported.", "Add Part",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string partName = ShowInputDialog("Add Part", "Enter the name for the new part:");
            if (string.IsNullOrWhiteSpace(partName))
                return;

            // New parts start at ID 1 (ID 0 is common properties)
            int newPartId = cls.Parts.Count > 0
                ? Math.Max(1, cls.Parts.Keys.Max() + 1)
                : 1;

            var newPart = new StylePart
            {
                PartId = newPartId,
                PartName = partName.Trim()
            };

            var commonState = new StyleState
            {
                StateId = 0,
                StateName = "Common"
            };

            newPart.States[0] = commonState;
            cls.Parts[newPartId] = newPart;

            m_classView.AddPartNode(classNode, newPart);
        }

        private void OnAddState(object sender, EventArgs e)
        {
            if (m_style == null)
                return;

            TreeNode partNode;
            StyleClass cls;
            StylePart part;
            if (!TryGetSelectedPartNode(out partNode, out cls, out part))
            {
                MessageBox.Show("Please select a part first.", "Add State",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string stateName = ShowInputDialog("Add State", "Enter the name for the new state:");
            if (string.IsNullOrWhiteSpace(stateName))
                return;

            // Find next available state ID
            int newStateId = 0;
            if (part.States.Count > 0)
                newStateId = part.States.Keys.Max() + 1;

            var newState = new StyleState
            {
                StateId = newStateId,
                StateName = stateName.Trim()
            };

            part.States[newStateId] = newState;
            m_classView.AddStateNode(partNode, newState);
        }

        private bool TryGetSelectedStateNode(out TreeNode stateNode, out StyleClass cls, out StylePart part, out StyleState state)
        {
            stateNode = null;
            cls = null;
            part = null;
            state = null;

            var selectedNode = m_classView.SelectedNode;
            if (selectedNode == null)
            {
                return false;
            }

            state = selectedNode.Tag as StyleState;
            if (state == null)
            {
                return false;
            }

            stateNode = selectedNode;
            part = selectedNode.Parent?.Tag as StylePart;
            cls = selectedNode.Parent?.Parent?.Tag as StyleClass;
            return part != null && cls != null;
        }

        private void ClearActiveSelectionViews()
        {
            m_selection = new Selection();
            m_selectedImage = UpdateImageView(null);
            m_imageView.SetActiveTabs(-1, 0);
            m_renderView.Image = null;
            m_propertyView.SetStylePart(null, null, null);
            UpdateStatusText("No selection");
        }

        private void OnRemoveClass(object sender, EventArgs e)
        {
            if (m_style == null)
                return;

            TreeNode classNode;
            StyleClass cls;
            if (!TryGetSelectedClassNode(out classNode, out cls))
            {
                MessageBox.Show("Please select a class first.", "Remove Class",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show($"Remove class '{cls.ClassName}' and all its parts/states/properties?",
                "Remove Class",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            m_style.Classes.Remove(cls.ClassId);
            classNode.Remove();
            ClearActiveSelectionViews();
        }

        private void OnRemovePart(object sender, EventArgs e)
        {
            if (m_style == null)
                return;

            TreeNode partNode;
            StyleClass cls;
            StylePart part;
            if (!TryGetSelectedPartNode(out partNode, out cls, out part))
            {
                MessageBox.Show("Please select a part first.", "Remove Part",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (part.PartId == 0)
            {
                MessageBox.Show("The Common Properties part cannot be removed.", "Remove Part",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show($"Remove part '{part.PartName}' and all its states/properties?",
                "Remove Part",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            cls.Parts.Remove(part.PartId);
            partNode.Remove();
            ClearActiveSelectionViews();
        }

        private void OnRemoveState(object sender, EventArgs e)
        {
            if (m_style == null)
                return;

            TreeNode stateNode;
            StyleClass cls;
            StylePart part;
            StyleState state;
            if (!TryGetSelectedStateNode(out stateNode, out cls, out part, out state))
            {
                MessageBox.Show("Please select a state first.", "Remove State",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (state.StateId == 0)
            {
                MessageBox.Show("The default Common state (ID 0) cannot be removed.", "Remove State",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (part.States.Count <= 1)
            {
                MessageBox.Show("A part must contain at least one state.", "Remove State",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show($"Remove state '{state.StateName}' and all its properties?",
                "Remove State",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            part.States.Remove(state.StateId);
            stateNode.Remove();
            DisplayPart(cls, part);
        }

        private sealed class BaseClassOption
        {
            public int ClassId { get; }
            public string DisplayName { get; }

            public BaseClassOption(int classId, string displayName)
            {
                ClassId = classId;
                DisplayName = displayName;
            }

            public override string ToString() => DisplayName;
        }

        private bool ShowAddClassDialog(out string className, out int baseClassId)
        {
            className = null;
            baseClassId = -1;

            var baseClassOptions = new List<BaseClassOption>
            {
                new BaseClassOption(-1, "(No explicit base class)")
            };

            bool filterToCompatibleBaseClasses = m_style.HasBaseClassMap;
            foreach (var cls in m_style.Classes.OrderBy(kv => kv.Value.ClassName, StringComparer.OrdinalIgnoreCase))
            {
                // Session-added classes are always available as base class options,
                // even if they don't have entries in the original BCMAP structure.
                // This allows newly added classes to be used as base classes for other
                // new classes during the same editing session.
                bool isSessionAdded = m_sessionAddedClassIds.Contains(cls.Key);
                if (filterToCompatibleBaseClasses && !isSessionAdded && !m_style.CanUseClassAsBaseClass(cls.Key))
                {
                    continue;
                }

                string displayName = $"{cls.Value.ClassName} (#{cls.Key})";
                if (isSessionAdded)
                {
                    displayName += " [New]";
                }
                baseClassOptions.Add(new BaseClassOption(cls.Key, displayName));
            }

            using (var form = new Form())
            using (var lbClassName = new Label())
            using (var tbClassName = new TextBox())
            using (var lbBaseClass = new Label())
            using (var cbBaseClass = new ComboBox())
            using (var btOk = new Button())
            using (var btCancel = new Button())
            {
                form.Text = "Add Class";
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.ClientSize = new Size(420, 145);
                form.AcceptButton = btOk;
                form.CancelButton = btCancel;

                lbClassName.Text = "Enter the name for the new class:";
                lbClassName.SetBounds(10, 10, 400, 20);

                tbClassName.SetBounds(10, 32, 400, 20);

                lbBaseClass.Text = "Inherit from:";
                lbBaseClass.SetBounds(10, 58, 400, 20);

                cbBaseClass.DropDownStyle = ComboBoxStyle.DropDownList;
                cbBaseClass.SetBounds(10, 80, 400, 21);
                cbBaseClass.Items.AddRange(baseClassOptions.Cast<object>().ToArray());
                cbBaseClass.SelectedIndex = 0;

                btOk.Text = "OK";
                btOk.DialogResult = DialogResult.OK;
                btOk.SetBounds(240, 112, 80, 25);
                btOk.Enabled = false;

                btCancel.Text = "Cancel";
                btCancel.DialogResult = DialogResult.Cancel;
                btCancel.SetBounds(330, 112, 80, 25);

                tbClassName.TextChanged += (s, e) =>
                {
                    btOk.Enabled = !string.IsNullOrWhiteSpace(tbClassName.Text);
                };

                form.Controls.AddRange(new Control[]
                {
                    lbClassName,
                    tbClassName,
                    lbBaseClass,
                    cbBaseClass,
                    btOk,
                    btCancel
                });

                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    className = tbClassName.Text.Trim();
                    if (cbBaseClass.SelectedItem is BaseClassOption option)
                    {
                        baseClassId = option.ClassId;
                    }

                    return !string.IsNullOrWhiteSpace(className);
                }

                return false;
            }
        }

        private static string ShowInputDialog(string title, string prompt)
        {
            using (var form = new Form())
            using (var label = new Label())
            using (var textBox = new TextBox())
            using (var btOk = new Button())
            using (var btCancel = new Button())
            {
                form.Text = title;
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.ClientSize = new Size(300, 100);
                form.AcceptButton = btOk;
                form.CancelButton = btCancel;

                label.Text = prompt;
                label.SetBounds(10, 10, 280, 20);

                textBox.SetBounds(10, 35, 280, 20);

                btOk.Text = "OK";
                btOk.DialogResult = DialogResult.OK;
                btOk.SetBounds(120, 65, 80, 25);

                btCancel.Text = "Cancel";
                btCancel.DialogResult = DialogResult.Cancel;
                btCancel.SetBounds(210, 65, 80, 25);

                form.Controls.AddRange(new Control[] { label, textBox, btOk, btCancel });

                if (form.ShowDialog() == DialogResult.OK)
                    return textBox.Text;
                return null;
            }
        }

        private void OnSearchClicked(object sender, EventArgs e)
        {
            if (!m_searchDialog.Visible)
            {
                m_searchDialog.Show(this);
                if (m_searchDialog.StartPosition == FormStartPosition.CenterParent)
                {
                    var x = Location.X + (Width - m_searchDialog.Width) / 2;
                    var y = Location.Y + (Height - m_searchDialog.Height) / 2;
                    m_searchDialog.Location = new Point(Math.Max(x, 0), Math.Max(y, 0));
                }
            }
        }

        private void OnSearchNextItem(SearchDialog.SearchMode mode, IDENTIFIER type, string search)
        {
            if (m_style == null)
                return;

            object searchObj = null;
            if (mode == SearchDialog.SearchMode.Property)
            {
                searchObj = MakeObjectFromSearchString(type, search);
                if (searchObj == null)
                {
                    string typeString = VisualStyleProperties.PROPERTY_INFO_MAP[(int)type].Name;
                    MessageBox.Show($"\"{search}\" doesn't seem to be a valid {typeString} property!", ""
                        , MessageBoxButtons.OK
                        , MessageBoxIcon.Warning);
                    return;
                }
            }

            // includeSelectedNode = false, because we would get stuck.
            var node = m_classView.FindNextNode(false, (_node) =>
            {
                switch (mode)
                {
                    case SearchDialog.SearchMode.Name:
                        return _node.Text.ToUpper().Contains(search.ToUpper());
                    case SearchDialog.SearchMode.Property:
                        {
                            StylePart part = _node.Tag as StylePart;
                            if (part != null)
                            {
                                return part.States.Any((kvp) =>
                                {
                                    return kvp.Value.Properties.Any((p) =>
                                    {
                                        return p.Header.typeID == (int)type &&
                                            p.GetValue().Equals(searchObj);
                                    });
                                });
                            }
                        }
                        return false;
                    default: return false;
                }
            });

            if(node == null)
            {
                MessageBox.Show($"No further match for \"{search}\" !\nSearch will begin from top again.", ""
                    , MessageBoxButtons.OK
                    , MessageBoxIcon.Information);
            }
        }

        private void OnReplaceItem(SearchDialog.ReplaceMode mode, IDENTIFIER type, string search, string replacement)
        {
            if (m_style == null)
                return;

            var searchObj = MakeObjectFromSearchString(type, search);
            if (searchObj == null)
            {
                string typeString = VisualStyleProperties.PROPERTY_INFO_MAP[(int)type].Name;
                MessageBox.Show($"\"{search}\" doesn't seem to be a valid {typeString} property!", ""
                    , MessageBoxButtons.OK
                    , MessageBoxIcon.Warning);
                return;
            }

            var replacementObj = MakeObjectFromSearchString(type, replacement);
            if (replacementObj == null)
            {
                string typeString = VisualStyleProperties.PROPERTY_INFO_MAP[(int)type].Name;
                MessageBox.Show($"\"{replacementObj}\" doesn't seem to be a valid {typeString} property!", ""
                    , MessageBoxButtons.OK
                    , MessageBoxIcon.Warning);
                return;
            }

            // includeSelectedNode = true, since we replace the matches we can't get stuck.
            // Also, we need to exhaust all matches of the nodes.
            var node = m_classView.FindNextNode(true, (_node) =>
            {
                StylePart part = _node.Tag as StylePart;
                if (part != null)
                {
                    return part.States.Any((kvp) =>
                    {
                        return kvp.Value.Properties.Any((p) =>
                        {
                            bool isMatch = p.Header.typeID == (int)type &&
                                           p.GetValue().Equals(searchObj);
                            if (isMatch)
                            {
                                p.SetValue(replacementObj);
                            }

                            return isMatch;
                        });
                    });
                }
                else return false;
            });

            if (node == null)
            {
                MessageBox.Show($"No further match for \"{search}\" !\nSearch & replace will begin from top again.", ""
                    , MessageBoxButtons.OK
                    , MessageBoxIcon.Information);
            }
        }

        private object MakeObjectFromSearchString(IDENTIFIER type, string search)
        {
            string ns = search.Replace(" ", "");
            string[] components = ns.Split(new char[] { ',', ';' }); ;

            try
            {
                switch (type)
                {
                    case IDENTIFIER.SIZE:
                    case IDENTIFIER.FILENAME:
                    case IDENTIFIER.FONT:
                        {
                            if (components.Length != 1) return null;
                            return Convert.ToInt32(components[0]);
                        }
                    case IDENTIFIER.POSITION:
                        {
                            if (components.Length != 2) return null;
                            return new Size(
                                Convert.ToInt32(components[0]),
                                Convert.ToInt32(components[1]));
                        }
                    case IDENTIFIER.COLOR:
                        {
                            if (components.Length != 3) return null;
                            return Color.FromArgb(
                                Convert.ToInt32(components[0]),
                                Convert.ToInt32(components[1]),
                                Convert.ToInt32(components[2]));
                        }
                    case IDENTIFIER.MARGINS:
                    case IDENTIFIER.RECTTYPE:
                        {
                            if (components.Length != 4) return null;
                            return new Margins(
                                Convert.ToInt32(components[0]),
                                Convert.ToInt32(components[1]),
                                Convert.ToInt32(components[2]),
                                Convert.ToInt32(components[3]));
                        }
                    default: return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void OnDocumentationClicked(object sender, EventArgs e)
        {
            try
            {
                Process.Start("https://github.com/nptr/msstyleEditor/wiki/Introduction");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error opening help page!", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnLicenseClicked(object sender, EventArgs e)
        {
            var dlg = new LicenseDialog();
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.ShowDialog();
        }

        private void OnAboutClicked(object sender, EventArgs e)
        {
            var dlg = new AboutDialog();
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.ShowDialog();
        }

        #endregion

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F))
            {
                OnSearchClicked(this, null);
                return true;
            }
            else if (keyData >= (Keys.Control | Keys.D1) && keyData <= (Keys.Control | Keys.D9))
            {
                Keys numberKey = keyData & ~Keys.Control;
                m_imageView.SetActiveTabIndex((int)numberKey - 0x30 - 1);
            }

            return base.ProcessDialogKey(keyData);
        }

        private void OnMainWindowLoad(object sender, EventArgs e)
        {
            RegistrySettings settings = new RegistrySettings();
            if (!settings.HasConfirmedWarning)
            {
                if (MessageBox.Show("Modifying themes can break the operating system!\r\n\r\n" +
                    "Make sure you have a recent system restore point. Only proceed if you understand " +
                    "the risk and can deal with technical problems."

                    , "msstyleEditor - Risk Warning", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK)
                {
                    settings.HasConfirmedWarning = true;
                }
                else
                {
                    Close();
                }
            }
        }

        private void OnImageSelectIndex(object sender, EventArgs e)
        {
            if (m_selection.Part == null)
            {
                return;
            }

            var it = m_selection.Part.GetImageProperties();
            var imgProp = it.ElementAtOrDefault(m_imageView.SelectedIndex);
            if (imgProp != null)
            {
                m_selectedImage = UpdateImageView(imgProp);
            }
        }

        private void OnSystemColorsChanged(object sender, EventArgs e)
        {
            // HACK: In order to set the new theme for the dockpanel, we'd have to
            // close all windows and recreate them (with all the state). So instead,
            // we only fix the most prominent mismatch manually, which is the dockpanel
            // backcolor(s).
            dockPanel.Theme.ColorPalette.MainWindowActive.Background = SystemColors.Control;
            foreach (Control c in dockPanel.Controls)
            {
                c.BackColor = SystemColors.Control;
            }
        }
    }
}
