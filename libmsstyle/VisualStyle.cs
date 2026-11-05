using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

namespace libmsstyle
{
    public class VisualStyle : IDisposable
    {
        private IntPtr m_moduleHandle;

        private string m_stylePath;
        public string Path
        {
            get { return m_stylePath; }
        }

        private Platform m_platform;
        public Platform Platform
        {
            get { return m_platform; }
        }

        private Dictionary<int, StyleClass> m_classes;
        public Dictionary<int, StyleClass> Classes
        {
            get { return m_classes; }
        }

        private List<TimingFunction> m_timingFunctions;
        public List<TimingFunction> TimingFunctions
        {
            get { return m_timingFunctions; }
        }

        private List<Animation> m_animations;
        public List<Animation> Animations
        {
            get { return m_animations; }
        }

        private int m_numProps;
        public int NumProperties
        {
            get { return m_numProps; }
        }

        private Dictionary<int, string> m_stringTable;
        public Dictionary<int, string> PreferredStringTable
        {
            get { return m_stringTable; }
        }

        private Dictionary<int, Dictionary<int, string>> m_stringTables = new Dictionary<int, Dictionary<int, string>>();

        public Dictionary<int, Dictionary<int, string>> StringTables
        {
            get { return m_stringTables; }
        }

        private Dictionary<StyleResource, string> m_resourceUpdates;

        private ushort m_resourceLanguage;

        private bool m_classmapDirty = false;
        public void MarkClassmapDirty() { m_classmapDirty = true; }

        private byte[] m_originalCmap;
        private int m_cmapTotalEntries;
        private int m_originalClassCount;

        // CMAP entry alignment used by the current style (4 bytes on x86,
        // 8 bytes on x64 in uxtheme parser builds).
        private int m_cmapEntryAlignment = (IntPtr.Size >= 8) ? 8 : 4;

        private byte[] m_originalBcmap;
        private int m_bcmapClassIdOffset = 0;
        private int m_bcmapEntryCount = 0;
        private bool m_bcmapHasCountField = false;
        private List<int> m_originalBcmapParents = null;

        // Structured representations (new - replaces hex editing approach)
        private CMap m_cmapStruct;
        private BcMap m_bcmapStruct;

        // XP-specific: the selected color scheme
        private XpColorScheme m_xpColorScheme;

        // XP-specific: all available color schemes
        private List<XpColorScheme> m_xpColorSchemes;
        public List<XpColorScheme> XpColorSchemes
        {
            get { return m_xpColorSchemes; }
        }

        public VisualStyle()
        {
            m_stylePath = null;
            m_classes = new Dictionary<int, StyleClass>();
            m_stringTable = new Dictionary<int, string>();
            m_resourceUpdates = new Dictionary<StyleResource, string>();
            m_timingFunctions = new List<TimingFunction>();
            m_animations = new List<Animation>();
            m_numProps = 0;
            m_resourceLanguage = 0;
        }

        ~VisualStyle()
        {
            Dispose(false);
        }

        private bool m_disposed = false;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!m_disposed)
            {
                if (disposing)
                {
                    // managed resources
                }

                Win32Api.FreeLibrary(m_moduleHandle);
                m_moduleHandle = IntPtr.Zero;
                m_disposed = true;
            }
        }

        public void Save(string file, bool makeStandalone = false)
        {
            if (file != m_stylePath)
            {
                File.Copy(m_stylePath, file, true);
            }
            else throw new ArgumentException("Cannot overwrite the original file!");

            var updateHandle = Win32Api.BeginUpdateResource(file, false);
            if (updateHandle == IntPtr.Zero)
            {
                File.Delete(file);
                throw new IOException("Could not open the file for writing! (BeginUpdateResource)");
            }

            var moduleHandle = Win32Api.LoadLibraryEx(file, IntPtr.Zero, Win32Api.LoadLibraryFlags.LOAD_LIBRARY_AS_DATAFILE_EXCLUSIVE);
            if (moduleHandle == IntPtr.Zero)
            {
                Win32Api.EndUpdateResource(updateHandle, true);
                File.Delete(file);
                throw new IOException("Could not open the file for writing! (LoadLibraryEx)");
            }

            if (!SaveResources(moduleHandle, updateHandle))
            {
                Win32Api.FreeLibrary(moduleHandle);
                Win32Api.EndUpdateResource(updateHandle, true);
                File.Delete(file);
                throw new IOException("Could not save resources!");
            }

            if (!SaveAMap(moduleHandle, updateHandle))
            {
                Win32Api.FreeLibrary(moduleHandle);
                Win32Api.EndUpdateResource(updateHandle, true);
                File.Delete(file);
                throw new IOException("Could not save resources!");
            }

            if (m_classmapDirty)
            {
                if (!SaveClassmap(moduleHandle, updateHandle))
                {
                    Win32Api.FreeLibrary(moduleHandle);
                    Win32Api.EndUpdateResource(updateHandle, true);
                    File.Delete(file);
                    throw new IOException("Could not save class map!");
                }
            }

            if (!SaveProperties(moduleHandle, updateHandle))
            {
                Win32Api.FreeLibrary(moduleHandle);
                Win32Api.EndUpdateResource(updateHandle, true);
                File.Delete(file);
                throw new IOException("Could not save resources!");
            }

            try
            {
                SaveStringTable(moduleHandle, updateHandle, makeStandalone);
            }
            catch (Exception)
            {
                Win32Api.FreeLibrary(moduleHandle);
                Win32Api.EndUpdateResource(updateHandle, true);
                File.Delete(file);
                throw;
            }

            // close the module before calling EndUpdate(). If not
            // updating fails because the file is in use.
            Win32Api.FreeLibrary(moduleHandle);
            if (!Win32Api.EndUpdateResource(updateHandle, false))
            {
                File.Delete(file);
                throw new IOException("Could not write the changes to the file!");
            }

            Signature.AppendSignature(file, PackthemSignature);
        }

        /// <summary>the signature msstyleEditor appends to every saved style.</summary>
        internal static readonly byte[] PackthemSignature = new byte[]
        {
            0x72, 0x68, 0xC5, 0x5F, 0x1F, 0x00, 0xA4, 0x9B, 0xFF, 0x90, 0xB2, 0x94, 0x7F, 0x76, 0x38, 0x38,
            0x48, 0xD3, 0x9D, 0x80, 0xB2, 0x69, 0x0C, 0x52, 0xBC, 0x82, 0xDA, 0x1D, 0xC8, 0x54, 0xD4, 0xE3,
            0xD4, 0xC7, 0xA6, 0x79, 0xD1, 0x06, 0xBD, 0x44, 0xDD, 0x99, 0x57, 0x9C, 0x3E, 0xDD, 0xAD, 0xA6,
            0x58, 0x16, 0x49, 0xC7, 0x55, 0x93, 0x0E, 0xD1, 0x89, 0xB0, 0x71, 0x63, 0x2E, 0xE9, 0xDF, 0x02,
            0x26, 0x88, 0xEF, 0x56, 0x25, 0x5A, 0xA4, 0x04, 0xD5, 0xAB, 0x71, 0x31, 0xC2, 0x48, 0x29, 0xC4,
            0x13, 0xD0, 0x5B, 0x81, 0x3D, 0xCC, 0x27, 0x0A, 0xD6, 0xEE, 0x5C, 0x9E, 0x99, 0xE9, 0x53, 0x6D,
            0x87, 0x72, 0x41, 0x44, 0xAF, 0x61, 0xA0, 0x87, 0xE2, 0x3C, 0xE0, 0x62, 0x98, 0x26, 0xBF, 0xE7,
            0x80, 0xFF, 0x23, 0xCA, 0xF7, 0xC6, 0x34, 0x6C, 0x9A, 0xA8, 0xA1, 0xA6, 0xEE, 0xA4, 0xB6, 0xEE
        };


        private bool SaveResources(IntPtr moduleHandle, IntPtr updateHandle)
        {
            var updates = new List<KeyValuePair<StyleResource, byte[]>>();
            foreach (var res in m_resourceUpdates)
            {
                try
                {
                    updates.Add(new KeyValuePair<StyleResource, byte[]>(res.Key, File.ReadAllBytes(res.Value)));
                }
                catch (Exception)
                {
                    return false;
                }
            }
            updates.AddRange(m_resourceDataUpdates);

            foreach (var res in updates)
            {
                byte[] data = res.Value;
                string type = "";
                switch (res.Key.Type)
                {
                    case StyleResourceType.None:
                        continue;
                    case StyleResourceType.Image:
                        type = "IMAGE"; break;
                    case StyleResourceType.Atlas:
                        type = "STREAM"; break;
                }

                ushort lid = ResourceAccess.GetFirstLanguageId(moduleHandle, type, (uint)res.Key.ResourceId);
                if (lid == 0xFFFF)
                {
                    lid = m_resourceLanguage;
                }
                if (!Win32Api.UpdateResource(updateHandle, type, (uint)res.Key.ResourceId, lid, data, (uint)data.Length))
                {
                    return false;
                }
            }

            return true;
        }


        /// <summary>
        /// Serializes the property tree into the VARIANT/NORMAL record stream, exactly as Save() writes it.
        /// </summary>
        public byte[] SerializeProperties()
        {
            MemoryStream ms = new MemoryStream(4096);
            BinaryWriter bw = new BinaryWriter(ms);

            foreach (var cls in m_classes.OrderBy(c => c.Key))
            {
                foreach (var part in cls.Value.Parts.OrderBy(p => p.Key))
                {
                    foreach (var state in part.Value.States.OrderBy(s => s.Key))
                    {
                        // stable: a state can hold several records with one name id (DWMWindow's
                        // ATLASRECTs). List.Sort is unstable and reordered them differently from run to run.
                        var sorted = state.Value.Properties.OrderBy(p => p.Header.nameID).ToList();
                        state.Value.Properties.Clear();
                        state.Value.Properties.AddRange(sorted);

                        foreach (var prop in state.Value.Properties)
                        {
                            PropertyStream.WriteProperty(bw, prop);
                        }
                    }
                }
            }

            return ms.ToArray();
        }

        private bool SaveProperties(IntPtr moduleHandle, IntPtr updateHandle)
        {
            // lang id
            ushort lid = ResourceAccess.GetFirstLanguageId(moduleHandle, "VARIANT", "NORMAL");
            byte[] data = SerializeProperties();
            return Win32Api.UpdateResource(updateHandle, "VARIANT", "NORMAL", lid, data, (uint)data.Length);
        }

        private void SaveStringTable(IntPtr moduleHandle, IntPtr updateHandle, bool makeStandalone)
        {
            if (m_stringTable.Count == 0)
            {
                return;
            }

            if (makeStandalone)
            {
                // To make a .msstyle relocatable, we need to remove the MUI (Multilingual UI) resource
                // entries and store the string table that was in the .mui's in the .msstyle itself.
                // This allows us to apply the style from anywhere, not restricted to its folder and the
                // accompanying mui resources.

                // Disadvantage: The style might work for only one script, because the string
                // table defines (among other unimportant things) the fonts. If a latin user creates a theme
                // this way, it might not work for on a chinese windows because the visual style might reference
                // latin-only fonts, so chinese texts won't render.


                // Deleting the MUI is tricky:
                // - If we attempt to delete a non-existing resource, all subsequent resource calls
                //   will fail with ERROR_INTERNAL_ERROR.
                // - If we attempt to delete a resource from a MUI file, we get ERROR_NOT_SUPPORTED.
                // - If we attempt to delete via LANG_NEUTRAL we get ERROR_INVALID_PARAMETER.

                ushort langId = ResourceAccess.GetFirstLanguageId(moduleHandle, "MUI", 1);
                if (langId != 0xFFFF)
                {
                    if (!Win32Api.UpdateResource(updateHandle, "MUI", 1, langId, null, 0))
                    {
                        int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                        throw new Exception($"Deleting MUI for lang {langId} failed with error '{err}'!");
                    }
                }

                // Without the MUI, all updates should go into the .msstyles file rather.
                foreach (var table in m_stringTables)
                {
                    ResourceAccess.StringTable.Update(moduleHandle, updateHandle, table.Value, (ushort)table.Key);
                }
            }
            else
            {
                // We can't update the string table if it's in the .mui file. But since 
                // msstyleEditor doesn't allow editing the table, it not an immediate problem.
            }
        }

        private bool SaveAMap(IntPtr moduleHandle, IntPtr updateHandle)
        {
            if (m_animations.Count == 0 && m_timingFunctions.Count == 0)
                return true;

            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms);

            foreach (var item in m_timingFunctions)
            {
                item.Write(bw);
            }
            foreach (var item in m_animations)
            {
                item.Write(bw);
            }

            ushort lid = ResourceAccess.GetFirstLanguageId(moduleHandle, "AMAP", "AMAP");
            if (lid == 0xFFFF)
            {
                lid = m_resourceLanguage;
            }
            byte[] data = ms.ToArray();
            return Win32Api.UpdateResource(updateHandle, "AMAP", "AMAP", lid, data, (uint)data.Length);

        }

        private static int NormalizeCmapEntryAlignment(int alignment)
        {
            return alignment <= 4 ? 4 : 8;
        }

        private static void WriteCmapEntry(Stream stream, string value, int alignment)
        {
            // CMAP entries are UTF-16 strings terminated by NUL and aligned to either
            // 4 or 8 bytes, depending on the parser implementation.
            int entryAlignment = NormalizeCmapEntryAlignment(alignment);

            string safeValue = value ?? string.Empty;
            byte[] encoded = Encoding.Unicode.GetBytes(safeValue);

            int payloadSize = encoded.Length + 2; // include UTF-16 null terminator
            int paddedSize = (payloadSize + (entryAlignment - 1)) & ~(entryAlignment - 1);

            stream.Write(encoded, 0, encoded.Length);
            stream.WriteByte(0);
            stream.WriteByte(0);

            int paddingBytes = paddedSize - payloadSize;
            for (int i = 0; i < paddingBytes; ++i)
            {
                stream.WriteByte(0);
            }
        }

        private static int DetectCmapEntryAlignment(byte[] cmap, int fallbackAlignment)
        {
            int maxZeroRun = 0;
            int currentRun = 0;
            for (int i = 0; i < (cmap?.Length ?? 0); ++i)
            {
                if (cmap[i] == 0)
                {
                    currentRun++;
                    if (currentRun > maxZeroRun)
                    {
                        maxZeroRun = currentRun;
                    }
                }
                else
                {
                    currentRun = 0;
                }
            }

            // Empirical discriminator:
            // - 4-byte aligned CMAP entries top out at ~5 consecutive zero bytes
            //   (terminator + optional 2-byte padding + next UTF-16 high byte).
            // - 8-byte aligned CMAP entries often exceed that.
            if (maxZeroRun > 5)
            {
                return 8;
            }

            if (maxZeroRun > 0)
            {
                return 4;
            }

            return NormalizeCmapEntryAlignment(fallbackAlignment);
        }

        private bool TryParseAlignedCmapEntries(byte[] cmap, int alignment, out List<string> entries)
        {
            entries = new List<string>();
            if (cmap == null || cmap.Length == 0 || (cmap.Length % 2) != 0)
            {
                return false;
            }

            int entryAlignment = NormalizeCmapEntryAlignment(alignment);

            int cursor = 0;
            while (cursor < cmap.Length)
            {
                // Ensure cursor is at even position for UTF-16LE
                if (cursor % 2 != 0)
                {
                    return false;  // Misaligned - invalid CMAP
                }

                int i = cursor;
                while (i + 1 < cmap.Length)
                {
                    if (cmap[i] == 0 && cmap[i + 1] == 0)
                    {
                        break;
                    }
                    i += 2;
                }

                // Check if we found a valid null terminator
                if (i + 1 >= cmap.Length || i < cursor)
                {
                    return false;
                }

                // Must have found an actual null terminator (i should be >= cursor)
                if (!(cmap[i] == 0 && cmap[i + 1] == 0))
                {
                    return false;
                }

                int stringBytes = i - cursor;
                string entry = stringBytes > 0
                    ? Encoding.Unicode.GetString(cmap, cursor, stringBytes)
                    : string.Empty;

                // Skip empty entries (causes blank lines in CMAP output)
                // Also skip entries that start after the found null terminator
                if (!string.IsNullOrEmpty(entry))
                {
                    entries.Add(entry);
                }

                int payloadBytes = stringBytes + 2; // include null terminator
                int entryBytes = (payloadBytes + (entryAlignment - 1)) & ~(entryAlignment - 1);
                if (entryBytes <= 0)
                {
                    return false;
                }

                cursor += entryBytes;
                if (cursor > cmap.Length)
                {
                    return false;
                }
            }

            return cursor == cmap.Length;
        }

        private List<string> ParseCmapEntries(byte[] cmap)
        {
            var entries = new List<string>();
            if (cmap == null || cmap.Length < 2)
            {
                return entries;
            }

            int first = 0;
            for (int i = 0; i + 1 < cmap.Length; i += 2)
            {
                if (cmap[i] == 0 && cmap[i + 1] == 0)
                {
                    int len = Math.Max(0, i - first);
                    // Skip empty entries (causes blank lines in CMAP output)
                    if (len > 0)
                    {
                        entries.Add(Encoding.Unicode.GetString(cmap, first, len));
                    }
                    first = i + 2;
                }
            }

            // Be tolerant of malformed CMAP blobs that are missing a final null terminator.
            int trailingBytes = cmap.Length - first;
            trailingBytes -= trailingBytes % 2;
            if (trailingBytes > 0)
            {
                string trailing = Encoding.Unicode.GetString(cmap, first, trailingBytes).TrimEnd('\0');
                if (!string.IsNullOrEmpty(trailing))
                {
                    entries.Add(trailing);
                }
            }

            return entries;
        }

        private bool SaveClassmap(IntPtr moduleHandle, IntPtr updateHandle)
        {
            // Preserve the original CMAP bytes exactly when valid, then append only
            // newly added classes using uxtheme-compatible alignment.
            var ms = new MemoryStream();

            if (m_originalCmap != null)
            {
                List<string> alignedEntries;
                if (TryParseAlignedCmapEntries(m_originalCmap, m_cmapEntryAlignment, out alignedEntries))
                {
                    ms.Write(m_originalCmap, 0, m_originalCmap.Length);
                }
                else
                {
                    int alternativeAlignment = m_cmapEntryAlignment == 8 ? 4 : 8;
                    if (TryParseAlignedCmapEntries(m_originalCmap, alternativeAlignment, out alignedEntries))
                    {
                        m_cmapEntryAlignment = alternativeAlignment;
                        ms.Write(m_originalCmap, 0, m_originalCmap.Length);
                    }
                    else
                    {
                        // Repair malformed CMAP blobs produced by older builds by rebuilding
                        // from loaded class names in class-id order.
                        for (int classId = 0; classId < m_originalClassCount; ++classId)
                        {
                            if (m_classes.TryGetValue(classId, out StyleClass cls))
                            {
                                WriteCmapEntry(ms, cls.ClassName, m_cmapEntryAlignment);
                            }
                        }
                    }
                }
            }

            // Append new classes (those with IDs beyond what was loaded from original CMAP)
            var newClasses = m_classes
                .Where(kv => kv.Key >= m_originalClassCount)
                .OrderBy(kv => kv.Key)
                .ToList();

            // Add new classes to structured CMAP
            foreach (var kv in newClasses)
            {
                // Skip empty or whitespace class names
                if (!string.IsNullOrWhiteSpace(kv.Value.ClassName))
                {
                    // Check if this class is already in the Entries (to prevent duplicates on re-save)
                    bool alreadyExists = m_cmapStruct.Entries.Any(e => e.ClassName == kv.Value.ClassName);
                    if (!alreadyExists)
                    {
                        m_cmapStruct.Entries.Add(new CMapEntry(kv.Value.ClassName));
                    }
                }
            }

            // Serialize CMAP from structured data (includes ALL classes, original + new)
            byte[] cmapData = m_cmapStruct.Serialize();
            ms.Write(cmapData, 0, cmapData.Length);

            ushort lid = ResourceAccess.GetFirstLanguageId(moduleHandle, "CMAP", "CMAP");
            if (lid == 0xFFFF)
            {
                lid = m_resourceLanguage;
            }

            if (!Win32Api.UpdateResource(updateHandle, "CMAP", "CMAP", lid, cmapData, (uint)cmapData.Length))
                return false;

            // Update BCMAP using structured data
            if (m_bcmapStruct != null && newClasses.Count > 0)
            {
                int originalEntryCount = m_bcmapEntryCount;
                List<int> originalParents = m_originalBcmapParents;
                bool hasCountField = m_bcmapHasCountField;

                // Check for WSB artifact - extra BCMAP entries for non-existent classes
                // These artifact entries cause issues when adding new classes because
                // new classes try to reuse these entries (which have wrong parent info)
                int artifactCount = GetWsArtifactCount();
                if (artifactCount > 0)
                {
                    // Remove WSB artifact entries from the end of the BCMAP
                    // These entries are for classes that don't exist in the CMAP
                    for (int i = 0; i < artifactCount; i++)
                    {
                        if (m_bcmapStruct.Entries.Count > 0)
                        {
                            m_bcmapStruct.Entries.RemoveAt(m_bcmapStruct.Entries.Count - 1);
                        }
                    }
                }

                // Add new class entries to structured BCMAP
                foreach (var kv in newClasses.OrderBy(x => x.Key))
                {
                    int effectiveBaseClassId = GetEffectiveBaseClassId(kv.Value);
                    int parentIndex = m_bcmapStruct.BaseClassIdToParentIndex(effectiveBaseClassId);
                    m_bcmapStruct.Entries.Add(new BcMapEntry(parentIndex));
                }

                // Serialize BCMAP from structured data (includes ALL entries)
                // Count = Entries.Count (automatic, no manual calculation!)
                ushort bcmapLid = ResourceAccess.GetFirstLanguageId(moduleHandle, "BCMAP", "BCMAP");
                if (bcmapLid == 0xFFFF)
                    bcmapLid = m_resourceLanguage;

                byte[] bcmapData = m_bcmapStruct.Serialize();
                if (!Win32Api.UpdateResource(updateHandle, "BCMAP", "BCMAP", bcmapLid, bcmapData, (uint)bcmapData.Length))
                    return false;

                // Update BCMAP entry count to prevent false WSB artifact detection on subsequent saves
                // This ensures m_bcmapEntryCount accurately reflects the current state
                m_bcmapEntryCount = m_bcmapStruct.Entries.Count;
            }

            return true;
        }
        public void Load(string file)
        {
            m_moduleHandle = Win32Api.LoadLibraryEx(file, IntPtr.Zero, Win32Api.LoadLibraryFlags.LOAD_LIBRARY_AS_DATAFILE_EXCLUSIVE);
            if (m_moduleHandle == IntPtr.Zero)
            {
                throw new Exception("Couldn't open file as PE resource!");
            }

            byte[] cmap = ResourceAccess.GetResource(m_moduleHandle, "CMAP", "CMAP");
            if (cmap == null)
            {
                // No CMAP — try XP format (TEXTFILE resources)
                byte[] themesIni = ResourceAccess.GetResource(m_moduleHandle, "TEXTFILE", "THEMES_INI");
                if (themesIni == null)
                {
                    throw new Exception("Style contains no class map and no THEMES_INI!");
                }

                LoadXp(themesIni);
                m_stylePath = file;
                return;
            }

            LoadClassmap(cmap);

            // Load BCMAP (Base Class Map) for class inheritance.
            // uxtheme reads this as: int32 count, followed by <count> int32 parent indices.
            // Each entry is parent index or -1.
            m_originalBcmap = ResourceAccess.GetResource(m_moduleHandle, "BCMAP", "BCMAP");
            LoadBaseClassMap(m_originalBcmap);

            // With the class map in place, we can reason about the platform.
            m_platform = DeterminePlatform();


            // There is no AMAP before Win8
            if (m_platform > Platform.Win7)
            {
                byte[] amap = ResourceAccess.GetResource(m_moduleHandle, "AMAP", "AMAP");
                if (amap == null)
                {
                    throw new Exception("Style contains no animation map!");
                }

                LoadAMap(amap);
            }


            BuildPropertyTree(m_platform);

            m_resourceLanguage = ResourceAccess.GetFirstLanguageId(m_moduleHandle, "VARIANT", "NORMAL");
            byte[] pmap = ResourceAccess.GetResource(m_moduleHandle, "VARIANT", "NORMAL");
            if (pmap == null)
            {
                throw new Exception("Style contains no properties!");
            }
            LoadProperties(pmap);


            // Try to get an overview of language resources.
            // Type: String Table, Name: 7 (typically style name & copyright)
            var l1 = ResourceAccess.GetAllLanguageIds(m_moduleHandle, "#" + Win32Api.RT_STRING, 7,
                Win32Api.EnumResourceFlags.RESOURCE_ENUM_MUI |
                Win32Api.EnumResourceFlags.RESOURCE_ENUM_LN);
            // Type: String Table, Name: 32 (typically font definitions)
            var l2 = ResourceAccess.GetAllLanguageIds(m_moduleHandle, "#" + Win32Api.RT_STRING, 32,
                Win32Api.EnumResourceFlags.RESOURCE_ENUM_MUI |
                Win32Api.EnumResourceFlags.RESOURCE_ENUM_LN);
            var langs = l1.Union(l2);

            // Load all tables for internal purposes.
            foreach (var lang in langs)
            {
                var table = new Dictionary<int, string>();
                ResourceAccess.StringTable.Load(m_moduleHandle, lang, table);
                m_stringTables[lang] = table;
            }

            // Get users preferred language for display purposes.
            // If we don't have it, choose any.
            int uiLang = System.Threading.Thread.CurrentThread.CurrentUICulture.LCID;
            if (!m_stringTables.TryGetValue(uiLang, out m_stringTable))
            {
                var kvp = m_stringTables.FirstOrDefault((t) => t.Value.Count > 0);
                m_stringTable = kvp.Value ?? new Dictionary<int, string>();
            }

            m_stylePath = file;
        }

        void LoadXp(byte[] themesIniData)
        {
            m_platform = Platform.WinXP;
            m_xpColorSchemes = XpStyleIniParser.ParseThemesIni(themesIniData);

            if (m_xpColorSchemes.Count == 0)
            {
                throw new Exception("No color schemes found in THEMES_INI!");
            }

            // What XP shows comes from the string table packthem wrote, not from THEMES_INI's DisplayName lines
            // (themeldr tmutils.cpp _EnumThemeColors/_EnumThemeSizes, uxtheme GetThemeDocumentationProperty):
            // color i = 1000+i, size i = 3000+i (index in COLORNAMES/SIZENAMES), theme name = 5000. Authors set them
            // freely: Flarn's sizes read "HaxrCorp S8 / Tahoma / Trebuchet MS", appows' "segoe ui / corbel / arial".
            var packed = new Dictionary<int, string>();
            foreach (var lang in ResourceAccess.GetAllLanguageIds(m_moduleHandle, "#" + Win32Api.RT_STRING, 63,
                Win32Api.EnumResourceFlags.RESOURCE_ENUM_LN).Take(1))
                ResourceAccess.StringTable.Load(m_moduleHandle, lang, packed);
            string Packed(int id) => packed.TryGetValue(id, out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
            var colorNames = XpNameList("COLORNAMES");
            var sizeNames = XpNameList("SIZENAMES");
            foreach (var scheme in m_xpColorSchemes)
            {
                int ci = colorNames.FindIndex(n => n.Equals(scheme.InternalName, StringComparison.OrdinalIgnoreCase));
                if (ci >= 0 && Packed(1000 + ci) is string cd) scheme.DisplayName = cd;
                foreach (var size in scheme.Sizes)
                {
                    int si = sizeNames.FindIndex(n => n.Equals(size.InternalName, StringComparison.OrdinalIgnoreCase));
                    if (si >= 0 && Packed(3000 + si) is string sd) size.DisplayName = sd;
                }
                int ni = sizeNames.FindIndex(n => n.Equals("NormalSize", StringComparison.OrdinalIgnoreCase));
                scheme.NormalSizeDisplayName = ni >= 0 ? Packed(3000 + ni) : null;
            }
            XpThemeDisplayName = Packed(5000) ?? XpStyleIniParser.ThemeDisplayName(themesIniData);

            // Default to first scheme; caller can call LoadXpWithScheme() to switch
            LoadXpWithScheme(m_xpColorSchemes[0]);
        }

        /// <summary>packthem's COLORNAMES/SIZENAMES resource: null-separated internal names in index order.</summary>
        private List<string> XpNameList(string type)
        {
            var data = ResourceAccess.GetResource(m_moduleHandle, type, 1);
            if (data == null) return new List<string>();
            return Encoding.Unicode.GetString(data).Split('\0').Where(s => s.Length > 0).ToList();
        }

        /// <summary>
        /// Loads or reloads the XP style with the specified color scheme.
        /// Called initially from LoadXp() and can be called again when user selects a different scheme.
        ///
        /// After parsing, XP's text references are turned into what a packed win10 style holds, so
        /// the rest of libmsstyle (and the importer) reads them like any other style:
        /// IMAGEFILE*/GLYPHIMAGEFILE get a style-local IMAGE id whose bytes are the BITMAP converted
        /// to a premultiplied png with XP's transparency applied, FONT gets an id into a string table
        /// built from the INI font strings. The ids exist only in memory (<see cref="GetRawResource"/>).
        /// </summary>
        /// <param name="size">one of <see cref="XpColorScheme.Sizes"/>; null = NormalSize.</param>
        public void LoadXpWithScheme(XpColorScheme scheme, XpSize size = null)
        {
            m_xpColorScheme = scheme;
            m_xpSize = size;
            m_classes.Clear();
            m_xpImages.Clear();
            m_xpDiagnostics.Clear();
            m_numProps = 0;

            string iniName = size?.IniResourceName ?? scheme.IniResourceName;
            byte[] iniData = ResourceAccess.GetResource(m_moduleHandle, "TEXTFILE", iniName);
            if (iniData == null)
            {
                throw new Exception($"Could not load INI resource '{iniName}'!");
            }

            XpStyleIniParser.ParseSchemeIni(iniData, m_classes, m_xpDiagnostics);

            var fonts = new Dictionary<int, string>();
            var fontIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var imageIds = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var cls in m_classes.Values)
            {
                foreach (var part in cls.Parts.Values)
                {
                    foreach (var state in part.States.Values)
                    {
                        foreach (var prop in state.Properties.ToList())
                        {
                            ++m_numProps;
                            string text = prop.GetValue() as string;
                            if (prop.Header.typeID == (int)IDENTIFIER.FONT)
                            {
                                if (!fontIds.TryGetValue(text, out int fid))
                                {
                                    fid = XpFontIdBase + fontIds.Count;
                                    fontIds[text] = fid;
                                    fonts[fid] = text;
                                }
                                prop.SetValue(fid);
                                // the INI text set sizeInBytes from the string length; a packed FONT record
                                // declares a LOGFONTW (0x5C), and win10 uxtheme rejects the theme otherwise.
                                prop.Header.sizeInBytes = new PropertyHeader(prop.Header.nameID, prop.Header.typeID).sizeInBytes;
                            }
                            else if (prop.Header.typeID == (int)IDENTIFIER.FILENAME && XpImageIds.Contains(prop.Header.nameID))
                            {
                                int id = ResolveXpImage(cls, part.PartId, state.StateId, prop, text, imageIds);
                                if (id > 0)
                                {
                                    prop.SetValue(id);
                                    prop.Header.sizeInBytes = new PropertyHeader(prop.Header.nameID, prop.Header.typeID).sizeInBytes;
                                }
                                else state.Properties.Remove(prop);
                            }
                        }
                    }
                }
            }

            // xp imagefile.cpp: a missing IMAGELAYOUT means HORIZONTAL; win10 defaults to VERTICAL.
            // Write xp's default into every state that has a strip and inherits no layout, so the
            // meaning survives on win10.
            foreach (var cls in m_classes.Values)
                foreach (var part in cls.Parts.Values)
                    foreach (var state in part.States.Values)
                    {
                        if (!state.Properties.Any(p => p.Header.typeID == (int)IDENTIFIER.FILENAME && p.Header.nameID != (int)IDENTIFIER.GLYPHIMAGEFILE)) continue;
                        if (FindXpEffective(cls, part.PartId, state.StateId, IDENTIFIER.IMAGELAYOUT) != null) continue;
                        var layout = new StyleProperty(new PropertyHeader((int)IDENTIFIER.IMAGELAYOUT, (int)IDENTIFIER.ENUM)
                            { classID = cls.ClassId, partID = part.PartId, stateID = state.StateId });
                        layout.SetValue(1);
                        state.Properties.Add(layout);
                        ++m_numProps;
                    }

            m_stringTables.Clear();
            m_stringTables[XpStringLanguage] = fonts;
            m_stringTable = fonts;
        }

        private const int XpFontIdBase = 1000;
        private const int XpStringLanguage = 1033;

        // IMAGEFILE..IMAGEFILE5, STOCKIMAGEFILE, GLYPHIMAGEFILE (XP tmschema.h 3001-3008).
        internal static readonly HashSet<int> XpImageIds = new HashSet<int> { 3001, 3002, 3003, 3004, 3005, 3006, 3007, 3008 };

        // style-local IMAGE id -> png bytes, XP only.
        private readonly Dictionary<int, byte[]> m_xpImages = new Dictionary<int, byte[]>();
        private readonly List<string> m_xpDiagnostics = new List<string>();

        /// <summary>
        /// XP only: INI sections and properties XP's own parser would reject (unknown names, bad
        /// values) and image references without a bitmap. Each was skipped, nothing was guessed.
        /// </summary>
        public IReadOnlyList<string> XpDiagnostics => m_xpDiagnostics;

        public XpColorScheme XpColorScheme => m_xpColorScheme;
        /// <summary>XP only: the loaded size, null for NormalSize.</summary>
        public XpSize XpSize => m_xpSize;
        private XpSize m_xpSize;
        /// <summary>XP only: THEMES_INI [Documentation] DisplayName.</summary>
        public string XpThemeDisplayName { get; private set; }

        /// <summary>XP only: stores a png under a fresh style-local IMAGE id (the importer re-cuts strips this way).</summary>
        internal int AddXpImage(byte[] png)
        {
            if (m_platform != Platform.WinXP) throw new InvalidOperationException("in-memory images exist only for xp styles");
            int id = m_xpImages.Count == 0 ? 1 : m_xpImages.Keys.Max() + 1;
            m_xpImages[id] = png;
            return id;
        }

        private int ResolveXpImage(StyleClass cls, int part, int state, StyleProperty prop, string file, Dictionary<string, int> ids)
        {
            string resName = XpStyleIniParser.ImageFileToResourceName(file);
            // RT_BITMAP = "#2"
            byte[] dib = ResourceAccess.GetResource(m_moduleHandle, "#2", resName);
            if (dib == null)
            {
                m_xpDiagnostics.Add($"{cls.ClassName} part {part} state {state} {SemanticPropertyName(prop)} = {file}: no BITMAP {resName} in the style; property skipped");
                return 0;
            }

            Color? key = XpColorKey(cls, part, state, prop.Header.nameID);

            string k = resName + "|" + (key.HasValue ? key.Value.ToArgb().ToString("X8") : "-");
            if (ids.TryGetValue(k, out int id)) return id;
            byte[] png;
            try
            {
                png = ImageConverter.ConvertXpDibToPremultipliedPng(dib, key);
            }
            catch (Exception ex)
            {
                m_xpDiagnostics.Add($"{cls.ClassName} part {part} state {state}: BITMAP {resName} can't be converted ({ex.Message}); property skipped");
                return 0;
            }
            id = ids.Count + 1;
            ids[k] = id;
            m_xpImages[id] = png;
            return id;
        }

        /// <summary>
        /// The color key xp draws this image with, or null for none.
        /// uxtheme imagefile.cpp: TRANSPARENT/TRANSPARENTCOLOR for the background image,
        /// GLYPHTRANSPARENT/GLYPHTRANSPARENTCOLOR for the glyph; magenta when no color is set.
        /// With GlyphType = ImageGlyph and an ImageSelectType, ImageFile1-5 are sizes of the glyph
        /// (their parent is _GlyphInfo), so they take the glyph's transparency: ScrollBar ARROWBTN's
        /// 8bpp glyph strips otherwise keep their magenta.
        /// </summary>
        internal Color? XpColorKey(StyleClass cls, int part, int state, int nameId)
        {
            bool sizeSlot = nameId >= (int)IDENTIFIER.IMAGEFILE1 && nameId <= (int)IDENTIFIER.IMAGEFILE5;
            var gt = FindXpEffective(cls, part, state, IDENTIFIER.GLYPHTYPE);
            var ist = FindXpEffective(cls, part, state, IDENTIFIER.IMAGESELECTTYPE);
            bool imageGlyph = gt?.GetValue() is int g && g == XpSchema.Enums["GLYPHTYPE"]["IMAGEGLYPH"]
                && ist?.GetValue() is int s && s != XpSchema.Enums["IMAGESELECTTYPE"]["NONE"];
            bool glyph = nameId == (int)IDENTIFIER.GLYPHIMAGEFILE || (sizeSlot && imageGlyph);

            var tp = FindXpEffective(cls, part, state, glyph ? IDENTIFIER.GLYPHTRANSPARENT : IDENTIFIER.TRANSPARENT_);
            if (!(tp?.GetValue() is bool b && b)) return null;
            var tc = FindXpEffective(cls, part, state, glyph ? IDENTIFIER.GLYPHTRANSPARENTCOLOR : IDENTIFIER.TRANSPARENTCOLOR);
            return tc?.GetValue() is Color c ? c : Color.FromArgb(255, 0, 255);
        }

        private static string SemanticPropertyName(StyleProperty p) =>
            VisualStyleProperties.PROPERTY_INFO_MAP.TryGetValue(p.Header.nameID, out var info) ? info.Name : "PROP" + p.Header.nameID;

        // XP lookup order inside a section family: state, part state 0, class part 0, then globals.
        private StyleProperty FindXpEffective(StyleClass cls, int part, int state, IDENTIFIER name)
        {
            StyleProperty Local(StyleClass c, int p, int s) =>
                c.Parts.TryGetValue(p, out var sp) && sp.States.TryGetValue(s, out var ss)
                    ? ss.Properties.FirstOrDefault(x => x.Header.nameID == (int)name) : null;
            var hit = Local(cls, part, state) ?? Local(cls, part, 0) ?? Local(cls, 0, 0);
            if (hit != null) return hit;
            var globals = m_classes.Values.FirstOrDefault(c => c.ClassName.Equals("globals", StringComparison.OrdinalIgnoreCase));
            return globals == null ? null : Local(globals, 0, 0);
        }

        void LoadClassmap(byte[] cmap)
        {
            m_originalCmap = (byte[])cmap.Clone();
            m_classes.Clear();

            // Create structured CMAP
            m_cmapStruct = new CMap();

            int numFound = 0;
            List<string> entries;

            // Choose alignment for this style. x86 uxtheme builds use 4-byte CMAP
            // stepping; x64 builds use 8-byte stepping.
            m_cmapEntryAlignment = DetectCmapEntryAlignment(cmap, m_cmapEntryAlignment);
            m_cmapStruct.EntryAlignment = m_cmapEntryAlignment;

            bool cmapWasMalformed = !TryParseAlignedCmapEntries(cmap, m_cmapEntryAlignment, out entries);
            if (cmapWasMalformed)
            {
                int alternativeAlignment = m_cmapEntryAlignment == 8 ? 4 : 8;
                if (TryParseAlignedCmapEntries(cmap, alternativeAlignment, out entries))
                {
                    m_cmapEntryAlignment = alternativeAlignment;
                    m_cmapStruct.EntryAlignment = alternativeAlignment;
                    cmapWasMalformed = false;
                }
                else
                {
                    // Compatibility fallback for malformed CMAP resources.
                    entries = ParseCmapEntries(cmap);
                    // Mark dirty so a subsequent save can repair the CMAP layout.
                    m_classmapDirty = true;
                    m_cmapStruct.WasRepaired = true;
                }
            }

            foreach (string entry in entries)
            {
                if (!string.IsNullOrEmpty(entry))
                {
                    // Create StyleClass
                    StyleClass cls = new StyleClass
                    {
                        ClassId = numFound,
                        ClassName = entry
                    };
                    m_classes[numFound] = cls;

                    // Create CMapEntry
                    m_cmapStruct.Entries.Add(new CMapEntry(entry));

                    numFound++;
                }
            }

            m_cmapTotalEntries = entries.Count;
            m_originalClassCount = numFound;
        }

        public bool HasBaseClassMap
        {
            get { return m_originalBcmap != null && m_bcmapEntryCount > 0; }
        }

        public bool CanUseClassAsBaseClass(int classId)
        {
            if (m_bcmapStruct == null || m_bcmapStruct.Entries.Count == 0)
            {
                return false;
            }

            int mappedIndex = m_bcmapStruct.ClassIdToBcMapIndex(classId);
            return mappedIndex >= 0 && mappedIndex < m_bcmapStruct.Entries.Count;
        }

        public int GetEffectiveBaseClassId(StyleClass cls)
        {
            if (cls == null)
            {
                return -1;
            }

            if (cls.BaseClassId >= 0)
            {
                return m_classes.ContainsKey(cls.BaseClassId)
                    ? cls.BaseClassId
                    : -1;
            }

            return FindImplicitBaseClassId(cls.ClassName);
        }

        private int FindImplicitBaseClassId(string className)
        {
            if (string.IsNullOrWhiteSpace(className))
            {
                return -1;
            }

            int separatorIndex = className.LastIndexOf("::", StringComparison.Ordinal);
            if (separatorIndex < 0 || separatorIndex + 2 >= className.Length)
            {
                return -1;
            }

            string baseClassName = className.Substring(separatorIndex + 2);
            foreach (var cls in m_classes)
            {
                if (string.Equals(cls.Value.ClassName, baseClassName, StringComparison.OrdinalIgnoreCase))
                {
                    return cls.Key;
                }
            }

            return -1;
        }


        /// <summary>
        /// Detects if the theme has a WSB artifact - an extra BCMAP entry for a class that doesn't exist in CMAP.
        /// This happens when WSB saves a theme with an empty 4-byte slot at the end of CMAP.
        /// The BCMAP includes an entry for this non-existent class, which causes issues when adding new classes.
        /// </summary>
        /// <returns>True if WSB artifact is detected</returns>
        public bool HasWsArtifact()
        {
            if (m_bcmapEntryCount == 0 || m_originalClassCount == 0)
                return false;

            int specialClassCount = m_bcmapStruct != null ? m_bcmapStruct.SpecialClassCount : 4;
            int expectedBcMapEntryCount = m_originalClassCount - specialClassCount;

            // If BCMAP has more entries than expected, there's a WSB artifact
            return m_bcmapEntryCount > expectedBcMapEntryCount;
        }

        /// <summary>
        /// Gets the number of WSB artifact entries (extra BCMAP entries for non-existent classes).
        /// </summary>
        public int GetWsArtifactCount()
        {
            if (!HasWsArtifact())
                return 0;

            int specialClassCount = m_bcmapStruct != null ? m_bcmapStruct.SpecialClassCount : 4;
            int expectedBcMapEntryCount = m_originalClassCount - specialClassCount;

            return m_bcmapEntryCount - expectedBcMapEntryCount;
        }

        /// <summary>
        /// Parses BCMAP and detects whether it has a count field or is legacy format (raw array).
        /// Legacy format is used by WSB and other tools - just int32[] parent indices without count.
        /// New format includes an int32 count prefix.
        /// </summary>
        bool TryParseBaseClassMap(byte[] bcmap, int classCount, out int entryCount, out List<int> parents, out bool hasCountField)
        {
            entryCount = 0;
            parents = new List<int>();
            hasCountField = false;

            if (bcmap == null || bcmap.Length < 4)
            {
                return false;
            }

            int declaredEntryCount = BitConverter.ToInt32(bcmap, 0);
            int totalInts = bcmap.Length / 4;

            // Key insight: if first 4 bytes as count gives offset close to 4 (special classes),
            // it's likely a count. Otherwise treat as legacy format.
            // Special classes: "", "Scr", 0 (3 classes at the end that don't have BCMAP entries)
            int offsetWithCount = classCount - declaredEntryCount;
            int offsetWithoutCount = classCount - (totalInts - 1); // -1 because one int is the count itself

            // The "correct" offset for BCMAP is 4 (3 special classes: "", "Scr", and one more)
            // But we allow some flexibility for edge cases
            bool countOffsetValid = offsetWithCount == 4 || offsetWithCount == 3 || offsetWithCount == 5;
            bool noCountOffsetValid = offsetWithoutCount == 4 || offsetWithoutCount == 3 || offsetWithoutCount == 5;

            // Additional check: if declaredCount * 4 + 4 exactly equals bcmap.Length, it has a count
            bool countMatchesLength = declaredEntryCount >= 0 && (declaredEntryCount * 4 + 4) == bcmap.Length;

            // Determine format
            if (countOffsetValid && (countMatchesLength || declaredEntryCount > 0 && declaredEntryCount < classCount))
            {
                // Has count field
                hasCountField = true;
                entryCount = Math.Min(declaredEntryCount, (bcmap.Length - 4) / 4);

                for (int i = 0; i < entryCount; ++i)
                {
                    parents.Add(BitConverter.ToInt32(bcmap, 4 + i * 4));
                }
                return true;
            }
            else if (noCountOffsetValid || !countOffsetValid)
            {
                // Legacy format: no count field, just raw parent indices
                hasCountField = false;
                entryCount = totalInts;

                for (int i = 0; i < entryCount; ++i)
                {
                    parents.Add(BitConverter.ToInt32(bcmap, i * 4));
                }
                return true;
            }

            // Fallback: assume legacy format if we can't decide
            hasCountField = false;
            entryCount = totalInts;
            for (int i = 0; i < entryCount; ++i)
            {
                parents.Add(BitConverter.ToInt32(bcmap, i * 4));
            }
            return true;
        }

        void LoadBaseClassMap(byte[] bcmap)
        {
            int entryCount;
            List<int> parents;
            bool hasCountField;
            if (!TryParseBaseClassMap(bcmap, m_classes.Count, out entryCount, out parents, out hasCountField))
            {
                m_bcmapClassIdOffset = 0;
                m_bcmapEntryCount = 0;
                m_bcmapHasCountField = false;
                return;
            }

            m_bcmapEntryCount = entryCount;
            m_bcmapHasCountField = hasCountField;

            // Create structured BCMAP
            m_bcmapStruct = new BcMap();
            m_bcmapStruct.HasCountField = hasCountField;
            // Fix: Always use 4 for SpecialClassCount (the standard value for Vista+)
            // The calculated value can be wrong when WSB artifacts are present
            m_bcmapStruct.SpecialClassCount = 4;

            // Create BcMapEntry objects from loaded parent indices
            foreach (int parentIndex in parents)
            {
                m_bcmapStruct.Entries.Add(new BcMapEntry(parentIndex));
            }

            // BCMAP indices are in the internal class index space used by uxtheme.
            // Map them into our CMAP-based class IDs using a count-derived offset.
            m_bcmapClassIdOffset = Math.Max(0, m_classes.Count - entryCount);

            // Store the original parents for use during save
            m_originalBcmapParents = new List<int>();
            for (int i = 0; i < entryCount && i < parents.Count; ++i)
            {
                m_originalBcmapParents.Add(parents[i]);
            }

            for (int i = 0; i < entryCount; ++i)
            {
                int classId = m_bcmapStruct.BcMapIndexToClassId(i);
                if (!m_classes.TryGetValue(classId, out StyleClass cls))
                {
                    continue;
                }

                int parentIndex = m_bcmapStruct.Entries[i].ParentIndex;
                if (parentIndex >= 0)
                {
                    int parentClassId = m_bcmapStruct.BcMapIndexToClassId(parentIndex);
                    cls.BaseClassId = m_classes.ContainsKey(parentClassId)
                        ? parentClassId
                        : -1;
                }
                else
                {
                    cls.BaseClassId = -1;
                }
            }
        }

        private readonly List<string> m_loadDiagnostics = new List<string>();
        /// <summary>
        /// Problems found while parsing the property stream (skipped bytes, records that failed to
        /// decode, records pointing at unknown classes). Empty means every byte was accounted for.
        /// </summary>
        public IReadOnlyList<string> LoadDiagnostics => m_loadDiagnostics;

        void LoadProperties(byte[] pmap)
        {
            int cursor = 0;

            while (cursor < pmap.Length - 4)
            {
                int recordStart = cursor;
                if (IsZeroTail(pmap, cursor))
                {
                    break;
                }
                try
                {
                    StyleProperty prop = PropertyStream.ReadNextProperty(pmap, ref cursor);

                    StyleClass cls;
                    if (!m_classes.TryGetValue(prop.Header.classID, out cls))
                    {
                        throw new Exception($"property {prop.Header.nameID} references unknown class ID {prop.Header.classID}");
                    }

                    StylePart part;
                    if (!cls.Parts.TryGetValue(prop.Header.partID, out part))
                    {
                        part = new StylePart()
                        {
                            PartId = prop.Header.partID,
                            PartName = "Part " + prop.Header.partID
                        };

                        cls.Parts.Add(part.PartId, part);
                    }

                    StyleState state;
                    if (!part.States.TryGetValue(prop.Header.stateID, out state))
                    {
                        state = new StyleState()
                        {
                            StateId = prop.Header.stateID,
                            StateName = "State " + prop.Header.stateID
                        };

                        part.States.Add(state.StateId, state);
                    }

                    state.Properties.Add(prop);
                    ++m_numProps;
                }
                catch (Exception ex)
                {
                    m_loadDiagnostics.Add($"VARIANT offset {recordStart}: {ex.Message}");
                    // a decode failure that didn't move the cursor would spin forever.
                    if (cursor <= recordStart)
                    {
                        m_loadDiagnostics.Add($"VARIANT offset {recordStart}: stopped parsing, {pmap.Length - recordStart} bytes not loaded");
                        break;
                    }
                }
            }

            // the loop leaves up to 4 bytes alone; zero padding is fine, anything else isn't.
            if (cursor < pmap.Length && !IsZeroTail(pmap, cursor))
            {
                m_loadDiagnostics.Add($"VARIANT offset {cursor}: {pmap.Length - cursor} trailing bytes aren't a record or padding");
            }
        }

        private static bool IsZeroTail(byte[] data, int start)
        {
            for (int i = start; i < data.Length; ++i)
            {
                if (data[i] != 0) return false;
            }
            return true;
        }

        void LoadAMap(byte[] amap)
        {
            int cursor = 0;

            while (cursor < amap.Length - 4)
            {
                PropertyHeader header = new PropertyHeader(amap, cursor);

                cursor += 32;
                if (header.nameID == (int)IDENTIFIER.TIMINGFUNCTION)
                {
                    m_timingFunctions.Add(new TimingFunction(amap, cursor, header));
                    cursor += 24; // 20 bytes struct size, 4 bytes padding (= aligns to 8 byte?)
                }
                else if (header.nameID == (int)IDENTIFIER.ANIMATION)
                {
                    m_animations.Add(new Animation(amap, ref cursor, header));
                }
                else
                {
                    throw new Exception($"Unknown AMAP name ID: {header.nameID}");
                }
            }
        }

        Platform DeterminePlatform()
        {
            bool foundDWMTouch = false;
            bool foundDWMPen = false;
            bool foundW8Taskband = false;
            bool foundVistaQueryBuilder = false;
            bool foundTaskBand2Light_Taskband2 = false;

            foreach (var cls in m_classes)
            {
                if (cls.Value.ClassName == "DWMTouch")
                {
                    foundDWMTouch = true; continue;
                }
                if (cls.Value.ClassName == "DWMPen")
                {
                    foundDWMPen = true; continue;
                }
                if (cls.Value.ClassName == "W8::TaskbandExtendedUI")
                {
                    foundW8Taskband = true; continue;
                }
                if (cls.Value.ClassName == "QueryBuilder")
                {
                    foundVistaQueryBuilder = true; continue;
                }
                if (cls.Value.ClassName == "DarkMode::TaskManager")
                {
                    foundTaskBand2Light_Taskband2 = true; continue;
                }
            }

            if (foundTaskBand2Light_Taskband2)
                return Platform.Win11;
            else if (foundW8Taskband)
                return Platform.Win81;
            else if (foundDWMTouch || foundDWMPen)
                return Platform.Win10;
            else if (foundVistaQueryBuilder)
                return Platform.Vista;
            else return Platform.Win7;
        }

        void BuildPropertyTree(Platform p)
        {
            foreach (var cls in m_classes)
            {
                var partList = VisualStyleParts.Find(cls.Value.ClassName, p);
                foreach (var partDescription in partList)
                {
                    StylePart part = new StylePart()
                    {
                        PartId = partDescription.Id,
                        PartName = partDescription.Name,
                    };
                    cls.Value.Parts.Add(part.PartId, part);

                    foreach (var stateDescription in partDescription.States)
                    {
                        StyleState state = new StyleState()
                        {
                            StateId = stateDescription.Value,
                            StateName = stateDescription.Name,
                        };
                        part.States.Add(state.StateId, state);
                    }
                }
            }
        }

        public StyleResource GetResourceFromProperty(StyleProperty prop)
        {
            switch (prop.Header.typeID)
            {
                case (int)IDENTIFIER.FILENAME:
                case (int)IDENTIFIER.FILENAME_LITE:
                    {
                        byte[] imgData = GetRawResource("IMAGE", prop.Header.shortFlag & 0xFFFF);
                        return new StyleResource(imgData, prop.Header.shortFlag, StyleResourceType.Image);
                    }
                case (int)IDENTIFIER.DISKSTREAM:
                    {
                        byte[] data = ResourceAccess.GetResource(m_moduleHandle, "STREAM", (uint)prop.Header.shortFlag);
                        return new StyleResource(data, prop.Header.shortFlag, StyleResourceType.Atlas);
                    }
                default:
                    {
                        return null;
                    }
            }
        }

        /// <summary>
        /// Bytes of an IMAGE or STREAM resource as stored in the loaded file (queued updates ignored).
        /// XP styles have no IMAGE resources; their converted bitmaps are served from memory.
        /// </summary>
        public byte[] GetRawResource(string type, int id)
        {
            if (id <= 0 || id > 0xFFFF) return null;
            if (m_platform == Platform.WinXP)
                return type == "IMAGE" && m_xpImages.TryGetValue(id, out var png) ? png : null;
            return ResourceAccess.GetResource(m_moduleHandle, type, (uint)id);
        }

        public string GetQueuedResourceUpdate(int nameId, StyleResourceType type)
        {
            string path;
            var key = new StyleResource(null, nameId, type);
            if (m_resourceUpdates.TryGetValue(key, out path))
            {
                return path;
            }
            else return string.Empty;
        }


        public void QueueResourceUpdate(int nameId, StyleResourceType type, string pathToNew)
        {
            var key = new StyleResource(null, nameId, type);
            m_resourceUpdates[key] = pathToNew;
        }

        private readonly Dictionary<StyleResource, byte[]> m_resourceDataUpdates = new Dictionary<StyleResource, byte[]>();

        /// <summary>
        /// Writes these bytes as IMAGE/STREAM resource <paramref name="nameId"/> on the next Save().
        /// Used for resources that don't exist as a file on disk (imported or newly packed atlases).
        /// </summary>
        public void QueueResourceData(int nameId, StyleResourceType type, byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            m_resourceDataUpdates[new StyleResource(null, nameId, type)] = data;
        }
    }
}
