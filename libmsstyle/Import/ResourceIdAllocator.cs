using System;
using System.Collections.Generic;
using System.Linq;

namespace libmsstyle.Import
{
    /// <summary>
    /// Hands out IMAGE/STREAM ids for bytes copied into the template. Ids are never reused: a new
    /// resource always gets max(existing, allocated) + 1, and identical bytes already in the
    /// template (or added earlier in the same run) are shared instead of duplicated.
    /// Equal ids across two files mean nothing, so only content decides reuse.
    /// </summary>
    public class ResourceIdAllocator
    {
        private readonly Dictionary<string, HashSet<int>> m_used = new Dictionary<string, HashSet<int>>();
        private readonly Dictionary<string, int> m_byContent = new Dictionary<string, int>();
        private readonly Dictionary<string, byte[]> m_added = new Dictionary<string, byte[]>();

        public IReadOnlyDictionary<string, byte[]> Added => m_added;

        /// <param name="existing">(type, id, sha1 of bytes) for every resource already in the template. An id of 0 means
        /// "this resource has no numeric name" (the caller couldn't parse one): it is skipped, because a property's
        /// low header word reads 0 as an inline payload rather than a resource, so nothing may ever be handed out as 0.</param>
        public ResourceIdAllocator(IEnumerable<Tuple<string, int, string>> existing)
        {
            foreach (var e in existing)
            {
                if (e == null || e.Item2 <= 0) continue;
                Used(e.Item1).Add(e.Item2);
                string ck = e.Item1 + "/" + e.Item3;
                // first one wins so reuse is deterministic.
                if (e.Item3 != null && !m_byContent.ContainsKey(ck)) m_byContent[ck] = e.Item2;
            }
        }

        private HashSet<int> Used(string type)
        {
            if (!m_used.TryGetValue(type, out var s))
            {
                s = new HashSet<int>();
                m_used[type] = s;
            }
            return s;
        }

        public bool IsUsed(string type, int id) => Used(type).Contains(id);

        /// <returns>the id holding these bytes in the output and whether it already existed.</returns>
        public int Place(string type, byte[] data, out bool reused)
        {
            if (data == null || data.Length == 0) throw new ArgumentException("refusing to add an empty " + type + " resource");
            string sha = ResourceInventory.Sha1Hex(data);
            string ck = type + "/" + sha;
            if (m_byContent.TryGetValue(ck, out int id))
            {
                reused = true;
                return id;
            }

            var used = Used(type);
            int next = used.Count == 0 ? 1 : used.Max() + 1;
            // properties carry the id in the low word of the header field.
            if (next > 0xFFFF) throw new InvalidOperationException($"no free {type} resource id left");
            used.Add(next);
            m_byContent[ck] = next;
            m_added[type + "/" + next] = data;
            reused = false;
            return next;
        }
    }
}
