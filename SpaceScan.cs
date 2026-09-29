using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

class Node
{
    public string Name;
    public Node Parent;
    public List<Node> Dirs = new List<Node>();
    public long Size, Files;
    public bool Denied;

    public bool IsAbsolute { get { return Name.Length > 1 && (Name[1] == ':' || Name.StartsWith("\\\\")); } }
    public string FullPath { get { return Parent == null || IsAbsolute ? Name : Scanner.Join(Parent.FullPath, Name); } }
}

// A file shown in the tree or in the top-files list. Files aren't kept per-folder in memory, only these.
class FileEntry
{
    public Node Dir;
    public string Name;
    public long Size;
    public long Modified;  // Windows file time, 0 when unknown
    public int ParentRec;  // MFT scan: parent record, resolved to Dir once the tree exists
    public bool Summary;   // "... N smaller files" row, not a real file

    public string FullPath { get { return Scanner.Join(Dir.FullPath, Name); } }
}

// Per-scan aggregates: size by extension and the largest files.
static class Stats
{
    public const int TopN = 1000;
    public const string NoExt = "(no extension)";
    public static Dictionary<string, long[]> Ext = new Dictionary<string, long[]>(); // ext -> [size, count]
    public static List<FileEntry> Top = new List<FileEntry>();
    static long topMin;

    public static void Reset()
    {
        Ext = new Dictionary<string, long[]>();
        Top = new List<FileEntry>();
        topMin = 0;
    }

    public static string ExtOf(string name)
    {
        int d = name.LastIndexOf('.');
        return d > 0 ? name.Substring(d).ToLowerInvariant() : NoExt;
    }

    public static void AddExt(string ext, long size, long count)
    {
        long[] v;
        if (!Ext.TryGetValue(ext, out v)) Ext[ext] = v = new long[2];
        v[0] += size;
        v[1] += count;
    }

    public static bool WantsTop(long size) { return Top.Count < TopN || size > topMin; }

    public static void AddTop(FileEntry f)
    {
        Top.Add(f);
        if (Top.Count >= 2 * TopN) Trim();
    }

    public static void Trim()
    {
        Top.Sort((a, b) => b.Size.CompareTo(a.Size));
        if (Top.Count > TopN) Top.RemoveRange(TopN, Top.Count - TopN);
        topMin = Top.Count == TopN ? Top[TopN - 1].Size : 0;
    }
}

// Filter for the search view and for the duplicate finder's candidate pass.
class Query
{
    public string Name;              // substring, case-insensitive; null = any name
    public long MinSize;
    public DateTime? NotTouchedSince;
    public int Max = 50000;

    public bool MatchesMeta(long size, long modified)
    {
        if (size < MinSize) return false;
        return !NotTouchedSince.HasValue || (modified > 0 && DateTime.FromFileTimeUtc(modified) <= NotTouchedSince.Value);
    }

    public bool MatchesName(string name) { return Name == null || name.IndexOf(Name, StringComparison.OrdinalIgnoreCase) >= 0; }
}

// Scan exclusions. A rule with a backslash matches the full path (and everything below it),
// anything else matches a file or folder name. * and ? work in both.
static class Exclude
{
    class Rule { public Regex Rx; public bool ByPath; public Regex Leaf; }
    static Rule[] rules = new Rule[0];
    public static string[] Patterns = new string[0];

    public static void Set(IEnumerable<string> patterns)
    {
        Patterns = patterns.Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith("#")).ToArray();
        rules = Patterns.Select(x =>
        {
            bool byPath = x.IndexOf('\\') >= 0;
            string trimmed = x.TrimEnd('\\');
            string rx = "^" + Wild(trimmed) + (byPath ? "(\\\\.*)?$" : "$");
            var rule = new Rule { Rx = new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.Compiled), ByPath = byPath };
            if (byPath) // last segment: a file name can only match a path rule if it matches this
                rule.Leaf = new Regex("^" + Wild(trimmed.Substring(trimmed.LastIndexOf('\\') + 1)) + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            return rule;
        }).ToArray();
    }

    static string Wild(string pattern) { return Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", "."); }

    public static bool Any { get { return rules.Length > 0; } }
    public static bool AnyPaths { get { return rules.Any(r => r.ByPath); } }

    // Cheap pre-filter: could this file name be the last segment of a path rule?
    public static bool LeafMatch(string name)
    {
        foreach (var r in rules) if (r.ByPath && r.Leaf.IsMatch(name)) return true;
        return false;
    }

    public static bool MatchPath(string fullPath)
    {
        foreach (var r in rules) if (r.ByPath && r.Rx.IsMatch(fullPath)) return true;
        return false;
    }

    // fullPath may be null when only the name is known (MFT pass).
    public static bool Match(string name, string fullPath)
    {
        foreach (var r in rules)
        {
            if (r.ByPath) { if (fullPath != null && r.Rx.IsMatch(fullPath)) return true; }
            else if (r.Rx.IsMatch(name)) return true;
        }
        return false;
    }

    public static bool MatchesName(string name)
    {
        foreach (var r in rules) if (!r.ByPath && r.Rx.IsMatch(name)) return true;
        return false;
    }
}

static class Scanner
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstFileExW(string name, int infoLevel, out WIN32_FIND_DATAW data, int searchOp, IntPtr filter, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool FindNextFileW(IntPtr h, out WIN32_FIND_DATAW data);
    [DllImport("kernel32.dll")]
    static extern bool FindClose(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetFileAttributesW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    public const uint DIR = 0x10, REPARSE = 0x400;
    const int ERROR_FILE_NOT_FOUND = 2;

    public static volatile bool Cancel;
    public static long FilesSeen, Total; // Total > 0 only when progress is measurable (MFT)
    public static int DeniedCount;
    public static string Current = "";
    public static bool CollectExt = true; // off for partial rescans, which would double count

    public static string Join(string dir, string name) { return dir.EndsWith("\\") ? dir + name : dir + "\\" + name; }

    // \\?\ prefix: no MAX_PATH limit; UNC shares become \\?\UNC\server\share.
    public static string LongPath(string path) { return path.StartsWith(@"\\") ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path; }

    public static bool Exists(string path) { return GetFileAttributesW(LongPath(path)) != 0xFFFFFFFF; }

    // Reads through the long-path prefix, so hashing works past MAX_PATH as well.
    public static FileStream OpenRead(string path)
    {
        var h = CreateFileW(LongPath(path), 0x80000000 /*GENERIC_READ*/, 7 /*share read|write|delete*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0x08000000 /*SEQUENTIAL_SCAN*/, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception();
        return new FileStream(h, FileAccess.Read, 1 << 20);
    }

    // Enumerates one directory. Returns false if it could not be opened (access denied etc).
    public static bool Enum(string path, Action<string, uint, long, long> onEntry)
    {
        WIN32_FIND_DATAW d;
        IntPtr h = FindFirstFileExW(LongPath(path).TrimEnd('\\') + @"\*", 1 /*FindExInfoBasic*/, out d, 0, IntPtr.Zero, 2 /*LARGE_FETCH*/);
        if (h == new IntPtr(-1)) return Marshal.GetLastWin32Error() == ERROR_FILE_NOT_FOUND;
        try
        {
            do
            {
                if (Cancel) break;
                if (d.cFileName == "." || d.cFileName == "..") continue;
                onEntry(d.cFileName, d.dwFileAttributes, ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow,
                    ((long)d.ftLastWriteTime.dwHighDateTime << 32) | (uint)d.ftLastWriteTime.dwLowDateTime);
            } while (FindNextFileW(h, out d));
        }
        finally { FindClose(h); }
        return true;
    }

    // Classic walk, used for subfolders, network shares, non-NTFS, or when MFT access is denied.
    // ponytail: single-threaded; parallel per-subtree if network shares turn out slow.
    public static void Scan(Node n, string path)
    {
        Current = path;
        bool ok = Enum(path, (name, attr, size, modified) =>
        {
            if ((attr & DIR) != 0)
            {
                if ((attr & REPARSE) != 0) return; // junctions/symlinks: skip, avoids loops and double counting
                if (Exclude.Any && Exclude.Match(name, Join(path, name))) return;
                var c = new Node { Name = name, Parent = n };
                n.Dirs.Add(c);
                Scan(c, Join(path, name));
                n.Size += c.Size;
                n.Files += c.Files;
            }
            else
            {
                if (Exclude.Any && Exclude.Match(name, Join(path, name))) return;
                n.Size += size;
                n.Files++;
                FilesSeen++;
                if (CollectExt) Stats.AddExt(Stats.ExtOf(name), size, 1);
                if (Stats.WantsTop(size)) Stats.AddTop(new FileEntry { Dir = n, Name = name, Size = size, Modified = modified });
            }
        });
        if (!ok) { n.Denied = true; DeniedCount++; }
        n.Dirs.Sort((a, b) => b.Size.CompareTo(a.Size));
    }
}

// Fast path: reads the NTFS Master File Table directly (like WizTree / TreeSize Pro). Needs admin.
// ponytail: ~30 bytes of RAM per MFT record (≈1 GB for 30M files); fine on 64-bit.
class Mft
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFilePointerEx(SafeFileHandle h, long distance, IntPtr newPos, uint method);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle h, byte[] buf, int toRead, out int read, IntPtr overlapped);

    const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    const int RootRecord = 5, FirstUserRecord = 16;
    const byte InUse = 1, IsDir = 2, GoodName = 4, Excluded = 8;

    int recSize;
    long count;
    int[] parent, extId;
    long[] size;
    byte[] flags;
    string[] names; // only directories get a name; files are listed on demand
    readonly List<KeyValuePair<long, string>> excludeCandidates = new List<KeyValuePair<long, string>>();
    Query query;              // set => this pass collects matches instead of scan statistics
    List<FileEntry> matches;
    readonly Dictionary<string, int> extIndex = new Dictionary<string, int>();
    readonly List<string> extNames = new List<string> { Stats.NoExt };

    public static bool IsNtfsRoot(string path)
    {
        string root = Path.GetPathRoot(path);
        return root != null && root.Length == 3 && root.Equals(path, StringComparison.OrdinalIgnoreCase)
            && new DriveInfo(root).DriveType != DriveType.Network && new DriveInfo(root).DriveFormat == "NTFS";
    }

    public static Node Scan(string root) { return new Mft().Run(root); }

    // Finds files across a whole volume by re-reading the MFT: seconds even on a multi-TB drive.
    public static List<FileEntry> Collect(string root, Query q)
    {
        var m = new Mft { query = q, matches = new List<FileEntry>() };
        m.Run(root);
        return m.matches;
    }

    static void Read(SafeFileHandle h, long offset, byte[] buf, int n)
    {
        int read;
        if (!SetFilePointerEx(h, offset, IntPtr.Zero, 0) || !ReadFile(h, buf, n, out read, IntPtr.Zero)) throw new Win32Exception();
        if (read != n) throw new IOException("Incomplete read from the MFT");
    }

    // Restores the last 2 bytes of each stride from the update sequence array; false = torn/corrupt record.
    static bool Fixup(byte[] b, int o, int recSize)
    {
        int usaOfs = BitConverter.ToUInt16(b, o + 4), usaCnt = BitConverter.ToUInt16(b, o + 6);
        if (usaCnt < 2 || usaOfs + usaCnt * 2 > recSize) return false;
        int stride = recSize / (usaCnt - 1);
        for (int i = 1; i < usaCnt; i++)
        {
            int p = o + i * stride - 2;
            if (b[p] != b[o + usaOfs] || b[p + 1] != b[o + usaOfs + 1]) return false;
            b[p] = b[o + usaOfs + 2 * i];
            b[p + 1] = b[o + usaOfs + 2 * i + 1];
        }
        return true;
    }

    Node Run(string root)
    {
        using (var h = CreateFileW(@"\\.\" + root.TrimEnd('\\'), 0x80000000 /*GENERIC_READ*/, 3 /*share R|W*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero))
        {
            if (h.IsInvalid) throw new Win32Exception();
            var vd = new byte[128];
            int ret;
            if (!DeviceIoControl(h, FSCTL_GET_NTFS_VOLUME_DATA, IntPtr.Zero, 0, vd, vd.Length, out ret, IntPtr.Zero)) throw new Win32Exception();
            int cluster = BitConverter.ToInt32(vd, 44);
            recSize = BitConverter.ToInt32(vd, 48);
            long mftLen = BitConverter.ToInt64(vd, 56), mftLcn = BitConverter.ToInt64(vd, 64);

            // Record 0 is $MFT itself; its $DATA run list tells where the (possibly fragmented) MFT lives.
            var rec0 = new byte[Math.Max(recSize, cluster)];
            Read(h, mftLcn * cluster, rec0, rec0.Length);
            if (!Fixup(rec0, 0, recSize)) throw new IOException("MFT record 0 invalid");
            var runs = DataRuns(rec0, recSize);

            count = mftLen / recSize;
            parent = new int[count];
            extId = new int[count];
            size = new long[count];
            flags = new byte[count];
            names = new string[count];
            Scanner.Total = count;

            int chunk = Math.Max(cluster, 4 << 20) / cluster * cluster;
            var buf = new byte[chunk];
            long rec = 0;
            foreach (var run in runs)
            {
                long off = run.Key * cluster, left = run.Value * cluster;
                while (left > 0 && rec < count && !Scanner.Cancel)
                {
                    int n = (int)Math.Min(left, chunk);
                    Read(h, off, buf, n);
                    for (int i = 0; i + recSize <= n && rec < count; i += recSize, rec++)
                        ParseRecord(buf, i, rec);
                    off += n;
                    left -= n;
                    Scanner.FilesSeen = rec;
                }
            }
            return BuildTree(root);
        }
    }

    // Returns (LCN, cluster count) pairs of the unnamed $DATA attribute.
    static List<KeyValuePair<long, long>> DataRuns(byte[] b, int recSize)
    {
        for (int a = BitConverter.ToUInt16(b, 0x14); a + 8 <= recSize; )
        {
            uint type = BitConverter.ToUInt32(b, a);
            int len = BitConverter.ToInt32(b, a + 4);
            if (type == 0xFFFFFFFF || len <= 0) break;
            if (type == 0x80 && b[a + 8] != 0 && b[a + 9] == 0)
            {
                var runs = new List<KeyValuePair<long, long>>();
                long lcn = 0;
                for (int p = a + BitConverter.ToUInt16(b, a + 0x20); p < a + len && b[p] != 0; )
                {
                    int lenBytes = b[p] & 0xF, ofsBytes = b[p] >> 4;
                    p++;
                    long clusters = 0, delta = 0;
                    for (int i = 0; i < lenBytes; i++) clusters |= (long)b[p + i] << (8 * i);
                    p += lenBytes;
                    for (int i = 0; i < ofsBytes; i++) delta |= (long)b[p + i] << (8 * i);
                    if (ofsBytes > 0 && (b[p + ofsBytes - 1] & 0x80) != 0) delta -= 1L << (8 * ofsBytes); // sign-extend
                    p += ofsBytes;
                    lcn += delta;
                    runs.Add(new KeyValuePair<long, long>(lcn, clusters));
                }
                return runs;
            }
            a += len;
        }
        // ponytail: $MFT so fragmented its $DATA lives in an extension record; we fall back to the classic scan.
        throw new IOException("MFT too fragmented");
    }

    int ExtId(byte[] b, int v)
    {
        int len = b[v + 0x40], start = v + 0x42;
        for (int k = len - 1; k > 0; k--)
        {
            if (b[start + 2 * k] != '.' || b[start + 2 * k + 1] != 0) continue;
            string e = Encoding.Unicode.GetString(b, start + 2 * k, (len - k) * 2).ToLowerInvariant();
            int id;
            if (!extIndex.TryGetValue(e, out id)) { id = extNames.Count; extNames.Add(e); extIndex[e] = id; }
            return id;
        }
        return 0;
    }

    void ParseRecord(byte[] b, int o, long rec)
    {
        if (BitConverter.ToUInt32(b, o) != 0x454C4946 /*"FILE"*/ || !Fixup(b, o, recSize)) return;
        int recFlags = BitConverter.ToUInt16(b, o + 0x16);
        if ((recFlags & 1) == 0) return;
        long baseRef = BitConverter.ToInt64(b, o + 0x20) & 0xFFFFFFFFFFFF;
        long idx = baseRef != 0 ? baseRef : rec; // extension records carry attributes of their base record
        if (idx >= count) return;
        if (baseRef == 0) flags[idx] |= (byte)(InUse | ((recFlags & 2) != 0 ? IsDir : 0));

        int nameV = -1;
        long modified = 0;
        bool sizeHere = false;
        int end = o + recSize;
        for (int a = o + BitConverter.ToUInt16(b, o + 0x14); a + 8 <= end; )
        {
            uint type = BitConverter.ToUInt32(b, a);
            int len = BitConverter.ToInt32(b, a + 4);
            if (type == 0xFFFFFFFF || len <= 0 || a + len > end) break;
            bool resident = b[a + 8] == 0;
            if (type == 0x10 && resident) // $STANDARD_INFORMATION: last modified at +0x08
                modified = BitConverter.ToInt64(b, a + BitConverter.ToUInt16(b, a + 0x14) + 0x08);
            else if (type == 0x30 && resident && (flags[idx] & GoodName) == 0) // $FILE_NAME
            {
                int v = a + BitConverter.ToUInt16(b, a + 0x14);
                bool dir = (BitConverter.ToUInt32(b, v + 0x38) & 0x10000000) != 0;
                parent[idx] = (int)(BitConverter.ToInt64(b, v) & 0xFFFFFFFFFFFF);
                if (dir) names[idx] = Encoding.Unicode.GetString(b, v + 0x42, b[v + 0x40] * 2);
                if (b[v + 0x41] != 2) // prefer long name over DOS 8.3 name
                {
                    flags[idx] |= GoodName;
                    if (Exclude.Any)
                    {
                        string entry = NameOf(b, v);
                        if (Exclude.MatchesName(entry)) flags[idx] |= Excluded;
                        else if (!dir && Exclude.AnyPaths && Exclude.LeafMatch(entry))
                            excludeCandidates.Add(new KeyValuePair<long, string>(idx, entry)); // full path checked in BuildTree
                    }
                    nameV = v;
                    if (!dir && query == null) extId[idx] = ExtId(b, v);
                }
            }
            else if (type == 0x80 && b[a + 9] == 0) // unnamed $DATA (alternate streams ignored)
            {
                if (resident) { size[idx] = BitConverter.ToUInt32(b, a + 0x10); sizeHere = true; }
                else if (BitConverter.ToInt64(b, a + 0x10) == 0) { size[idx] = BitConverter.ToInt64(b, a + 0x30); sizeHere = true; } // first extent holds real size
            }
            a += len;
        }

        if (baseRef != 0 || (recFlags & 2) != 0 || rec < FirstUserRecord || nameV < 0 || !sizeHere) return;
        if (query == null)
        {
            if (Stats.WantsTop(size[idx]))
                Stats.AddTop(new FileEntry { ParentRec = parent[idx], Name = NameOf(b, nameV), Size = size[idx], Modified = modified });
        }
        else if (matches.Count < query.Max && query.MatchesMeta(size[idx], modified))
        {
            string name = NameOf(b, nameV);
            if (query.MatchesName(name)) matches.Add(new FileEntry { ParentRec = parent[idx], Name = name, Size = size[idx], Modified = modified });
        }
    }

    static string NameOf(byte[] b, int v) { return Encoding.Unicode.GetString(b, v + 0x42, b[v + 0x40] * 2); }

    Node BuildTree(string root)
    {
        var nodes = new Node[count];
        for (long i = 0; i < count; i++)
            if ((flags[i] & (InUse | IsDir | Excluded)) == (InUse | IsDir) && (i == RootRecord || (i >= FirstUserRecord && names[i] != null)))
                nodes[i] = new Node { Name = i == RootRecord ? root : names[i] };

        // Link folders first: file paths (and therefore path exclusions) only exist once parents are known.
        for (long i = FirstUserRecord; i < count; i++)
        {
            if ((flags[i] & (InUse | Excluded)) != InUse || nodes[i] == null) continue;
            var p = parent[i] < count ? nodes[parent[i]] : null;
            if (p == null) continue; // parent is a system/deleted dir: not reachable in Explorer either
            nodes[i].Parent = p;
            p.Dirs.Add(nodes[i]);
        }
        foreach (var c in excludeCandidates)
        {
            var dir = parent[c.Key] < count ? nodes[parent[c.Key]] : null;
            if (dir != null && Exclude.MatchPath(Scanner.Join(dir.FullPath, c.Value))) flags[c.Key] |= Excluded;
        }

        var extSize = new long[extNames.Count];
        var extCount = new long[extNames.Count];
        for (long i = FirstUserRecord; i < count; i++)
        {
            if ((flags[i] & (InUse | IsDir | Excluded)) != InUse || nodes[i] != null) continue;
            var p = parent[i] < count ? nodes[parent[i]] : null;
            if (p == null) continue;
            p.Size += size[i];
            p.Files++;
            if (query == null) { extSize[extId[i]] += size[i]; extCount[extId[i]]++; }
        }
        var found = query == null ? Stats.Top : matches;
        foreach (var t in found) t.Dir = t.ParentRec < count ? nodes[t.ParentRec] : null;
        found.RemoveAll(t => t.Dir == null);
        if (query == null)
        {
            for (int k = 0; k < extNames.Count; k++)
                if (extCount[k] > 0) Stats.AddExt(extNames[k], extSize[k], extCount[k]);
            Stats.Trim();
        }

        var rootNode = nodes[RootRecord];
        Sum(rootNode, root);
        return rootNode;
    }

    static void Sum(Node n, string path)
    {
        if (Exclude.Any) n.Dirs.RemoveAll(c => Exclude.Match(c.Name, Scanner.Join(path, c.Name)));
        foreach (var c in n.Dirs)
        {
            Sum(c, Scanner.Join(path, c.Name));
            n.Size += c.Size;
            n.Files += c.Files;
        }
        n.Dirs.Sort((a, b) => b.Size.CompareTo(a.Size));
    }
}

// Search: straight off the MFT when the scan root is a volume root, otherwise a walk of the scanned tree.
static class Finder
{
    public static List<FileEntry> Run(Node root, Query q)
    {
        if (Mft.IsNtfsRoot(root.FullPath))
            try { return Mft.Collect(root.FullPath, q); }
            catch (Exception) { } // no admin rights: fall back to walking
        var res = new List<FileEntry>();
        Walk(root, q, res);
        return res;
    }

    static void Walk(Node n, Query q, List<FileEntry> res)
    {
        if (Scanner.Cancel || res.Count >= q.Max) return;
        Scanner.Current = n.FullPath;
        Scanner.FilesSeen++;
        Scanner.Enum(n.FullPath, (name, attr, size, modified) =>
        {
            if ((attr & Scanner.DIR) == 0 && res.Count < q.Max && q.MatchesMeta(size, modified) && q.MatchesName(name))
                res.Add(new FileEntry { Dir = n, Name = name, Size = size, Modified = modified });
        });
        foreach (var c in n.Dirs) Walk(c, q, res);
    }
}

// Duplicates: same size, then same hash of the first 64 KB, then same hash of the whole file.
// ponytail: MD5 over identical sizes, not a byte-for-byte compare; collisions here are a theoretical risk, not a practical one.
static class Dupes
{
    const int Head = 64 * 1024;

    public static List<List<FileEntry>> Find(List<FileEntry> files, Action<long, long> progress)
    {
        var result = new List<List<FileEntry>>();
        var bySize = files.Where(f => f.Size > 0).GroupBy(f => f.Size).Where(g => g.Count() > 1).ToList();
        long total = bySize.Sum(g => g.Sum(f => Math.Min(f.Size, Head) + (g.Key > Head ? f.Size : 0))), done = 0;
        foreach (var g in bySize)
        {
            if (Scanner.Cancel) break;
            foreach (var head in g.GroupBy(f => Hash(f, Head)))
            {
                done += head.Sum(f => Math.Min(f.Size, Head));
                if (head.Key == null || head.Count() < 2) { progress(done, total); continue; }
                if (g.Key <= Head) result.Add(head.ToList());
                else
                {
                    foreach (var full in head.GroupBy(f => Hash(f, long.MaxValue)))
                        if (full.Key != null && full.Count() > 1) result.Add(full.ToList());
                    done += head.Sum(f => f.Size);
                }
                progress(done, total);
            }
        }
        result.Sort((a, b) => ((b.Count - 1) * b[0].Size).CompareTo((a.Count - 1) * a[0].Size));
        return result;
    }

    // Returns null for files that can't be read; those are left out rather than reported as duplicates.
    static string Hash(FileEntry f, long max)
    {
        try
        {
            using (var s = Scanner.OpenRead(f.FullPath))
            using (var md5 = MD5.Create())
            {
                if (max >= f.Size) return Convert.ToBase64String(md5.ComputeHash(s));
                var buf = new byte[max];
                int read = s.Read(buf, 0, buf.Length);
                return Convert.ToBase64String(md5.ComputeHash(buf, 0, read));
            }
        }
        catch (Exception) { return null; }
    }
}

// A saved scan: folder sizes only, gzipped text. Small enough to keep around, enough to compare against later.
static class Snapshot
{
    const char Sep = '\u0001'; // tabs are legal in NTFS names, this control char is not

    public static void Save(Node root, string path)
    {
        using (var fs = File.Create(path))
        using (var gz = new GZipStream(fs, CompressionMode.Compress))
        using (var w = new StreamWriter(gz, new UTF8Encoding(false)))
        {
            w.WriteLine("spacescan" + Sep + "1" + Sep + root.FullPath + Sep + DateTime.UtcNow.ToString("o"));
            Write(w, root, 0);
        }
    }

    static void Write(StreamWriter w, Node n, int depth)
    {
        w.WriteLine("{0}{1}{2}{1}{3}{1}{4}", depth, Sep, n.Size, n.Files, n.Name.Replace(Sep, ' '));
        foreach (var c in n.Dirs) Write(w, c, depth + 1);
    }

    public static Node Load(string path, out DateTime taken, out string rootPath)
    {
        using (var fs = File.OpenRead(path))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        using (var r = new StreamReader(gz))
        {
            var head = (r.ReadLine() ?? "").Split(Sep);
            if (head.Length < 4 || head[0] != "spacescan") throw new IOException("Not a SpaceScan snapshot");
            rootPath = head[2];
            taken = DateTime.Parse(head[3], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime();
            var stack = new Node[512];
            Node root = null;
            string line;
            while ((line = r.ReadLine()) != null)
            {
                var f = line.Split(Sep);
                if (f.Length < 4) continue;
                int depth = int.Parse(f[0], CultureInfo.InvariantCulture);
                if (depth < 0 || depth >= stack.Length) continue;
                var n = new Node { Size = long.Parse(f[1], CultureInfo.InvariantCulture), Files = long.Parse(f[2], CultureInfo.InvariantCulture), Name = f[3] };
                if (depth == 0) root = n;
                else
                {
                    var parent = stack[depth - 1];
                    if (parent == null) continue;
                    n.Parent = parent;
                    parent.Dirs.Add(n);
                }
                stack[depth] = n;
            }
            if (root == null) throw new IOException("Snapshot is empty");
            return root;
        }
    }
}

class Change
{
    public string Path;
    public long Before, After, FilesBefore, FilesAfter;
    public long Delta { get { return After - Before; } }
}

// Folder-by-folder diff between a saved scan and the current one.
static class Comparer
{
    public static List<Change> Run(Node before, Node after, int max)
    {
        var list = new List<Change>();
        Walk(before, after, (before ?? after).Name, list, max);
        list.Sort((a, b) => Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta)));
        return list;
    }

    static void Walk(Node b, Node a, string path, List<Change> list, int max)
    {
        if (list.Count >= max || Scanner.Cancel) return;
        long before = b == null ? 0 : b.Size, after = a == null ? 0 : a.Size;
        if (before != after)
            list.Add(new Change { Path = path, Before = before, After = after,
                FilesBefore = b == null ? 0 : b.Files, FilesAfter = a == null ? 0 : a.Files });

        var kids = new Dictionary<string, Node[]>(StringComparer.OrdinalIgnoreCase);
        if (b != null) foreach (var c in b.Dirs) kids[c.Name] = new[] { c, null };
        if (a != null)
            foreach (var c in a.Dirs)
            {
                Node[] pair;
                if (kids.TryGetValue(c.Name, out pair)) pair[1] = c;
                else kids[c.Name] = new[] { null, c };
            }
        foreach (var kv in kids) Walk(kv.Value[0], kv.Value[1], Scanner.Join(path, kv.Key), list, max);
    }
}

static class Fmt
{
    public static string Size(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = b;
        int i = 0;
        while (Math.Abs(v) >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? b + " B" : v.ToString("0.00", CultureInfo.InvariantCulture) + " " + u[i];
    }

    public static string Pct(long part, long total) { return total > 0 ? (100.0 * part / total).ToString("0.0") + " %" : ""; }
}

// File-type groups shown instead of bare extensions.
static class Categories
{
    static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    static Categories()
    {
        Add("Video", ".mp4 .mkv .avi .mov .wmv .flv .webm .m4v .mpg .mpeg .ts .m2ts .vob .usm .bik");
        Add("Photos", ".jpg .jpeg .png .gif .bmp .tif .tiff .webp .heic .raw .cr2 .nef .arw .dng .psd .svg");
        Add("Audio", ".mp3 .flac .wav .aac .ogg .wma .m4a .opus .mid .aiff .pck");
        Add("Documents", ".pdf .doc .docx .xls .xlsx .ppt .pptx .txt .rtf .odt .ods .csv .md .epub .mobi");
        Add("Archives", ".zip .rar .7z .tar .gz .bz2 .xz .cab .lzma .zst .pak .bundle .asar");
        Add("Installers", ".exe .msi .msix .appx .msu .whl .deb .rpm .apk");
        Add("Disk images", ".iso .vhd .vhdx .vmdk .img .wim .esd .dmg .bin .qcow2");
        Add("Code", ".cs .js .ts .py .java .cpp .c .h .go .rs .php .rb .html .css .json .xml .yml .yaml .sql .ps1 .sh");
        Add("Libraries", ".dll .so .dylib .pdb .lib .obj .winmd .node");
        Add("Games & assets", ".assets .ress .resource .upk .uasset .blk .vpk .gcf .sav");
        Add("System & logs", ".sys .log .etl .dmp .cab .evtx .tmp .temp .bak .old .dat .db .sqlite .cache");
    }

    static void Add(string name, string exts)
    {
        foreach (var e in exts.Split(' ')) Map[e] = name;
    }

    public static string Of(string ext)
    {
        string name;
        return Map.TryGetValue(ext, out name) ? name : "Other";
    }

    // extension totals -> category totals
    public static List<KeyValuePair<string, long[]>> Group(Dictionary<string, long[]> ext)
    {
        var res = new Dictionary<string, long[]>();
        foreach (var kv in ext)
        {
            if (kv.Value[1] <= 0) continue;
            string cat = Of(kv.Key);
            long[] v;
            if (!res.TryGetValue(cat, out v)) res[cat] = v = new long[2];
            v[0] += kv.Value[0];
            v[1] += kv.Value[1];
        }
        return res.OrderByDescending(kv => kv.Value[0]).ToList();
    }
}

// Snapshots kept automatically, one per scanned root, so a later scan can be compared without saving anything by hand.
static class History
{
    public const int Keep = 12;

    public static string Dir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceScan", "history"); }
    }

    static string Key(string rootPath)
    {
        var safe = new StringBuilder();
        foreach (char c in rootPath.ToLowerInvariant()) safe.Append(char.IsLetterOrDigit(c) ? c : '-');
        return safe.ToString().Trim('-');
    }

    public static void Save(Node root)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            Snapshot.Save(root, Path.Combine(Dir, Key(root.FullPath) + "__" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".scan"));
            foreach (var old in List(root.FullPath).Skip(Keep)) File.Delete(old);
        }
        catch (Exception) { } // history is a convenience, never a reason to fail a scan
    }

    // Newest first.
    public static List<string> List(string rootPath)
    {
        try
        {
            if (!Directory.Exists(Dir)) return new List<string>();
            return Directory.GetFiles(Dir, Key(rootPath) + "__*.scan").OrderByDescending(f => f).ToList();
        }
        catch (Exception) { return new List<string>(); }
    }

    public static DateTime TakenAt(string file)
    {
        DateTime t;
        string stamp = Path.GetFileNameWithoutExtension(file);
        int at = stamp.LastIndexOf("__", StringComparison.Ordinal);
        return at >= 0 && DateTime.TryParseExact(stamp.Substring(at + 2), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)
            ? t : File.GetLastWriteTime(file);
    }
}

// The HTML report, shared by the app and the command line.
static class Report
{
    public static string Html(Node root, Dictionary<string, long[]> ext, List<FileEntry> top, List<Change> changes)
    {
        var dirs = new List<Node>();
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (var c in n.Dirs) { dirs.Add(c); stack.Push(c); }
        }

        var b = new StringBuilder();
        b.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>SpaceScan report</title><style>")
         .Append(":root{--ground:#0a0d13;--panel:rgba(255,255,255,.038);--edge:rgba(255,255,255,.085);--text:#e8eef6;--dim:#8a97aa;--dimmer:#5d6879;--accent:#4c8dff;--pink:#ff5c8a}")
         .Append("*{box-sizing:border-box}body{margin:0;background:var(--ground);color:var(--text);font:15px/1.6 'Segoe UI',system-ui,sans-serif}")
         .Append(".wrap{width:min(100% - 48px,1100px);margin:0 auto;padding:56px 0 80px}")
         .Append("h1{font-size:30px;margin:0;font-weight:600;letter-spacing:-.02em}.sub{color:var(--dim);margin-top:6px}")
         .Append(".dot{display:inline-block;width:10px;height:10px;border-radius:50%;background:var(--accent);box-shadow:0 0 14px rgba(76,141,255,.9);margin-right:10px}")
         .Append(".cards{display:flex;gap:14px;margin:28px 0 8px;flex-wrap:wrap}")
         .Append(".card{flex:1 1 200px;background:var(--panel);border:1px solid var(--edge);border-radius:14px;padding:16px 18px}")
         .Append(".card b{display:block;font-size:24px;font-weight:600;margin-top:4px}")
         .Append(".eyebrow{font-size:11.5px;letter-spacing:.14em;text-transform:uppercase;color:var(--dimmer)}")
         .Append("section{margin-top:44px}table{width:100%;border-collapse:collapse;margin-top:14px}")
         .Append("th{text-align:left;font-size:11.5px;letter-spacing:.14em;text-transform:uppercase;color:var(--dimmer);font-weight:600;padding:0 10px 8px}")
         .Append("td{padding:7px 10px;border-top:1px solid var(--edge);font-variant-numeric:tabular-nums}")
         .Append("td.n{text-align:right;color:var(--dim);white-space:nowrap}.bar{height:6px;border-radius:3px;background:var(--accent)}")
         .Append(".barcell{width:180px}.track{background:rgba(255,255,255,.085);border-radius:3px}")
         .Append(".up{color:var(--pink)}.down{color:var(--accent)}")
         .Append("</style></head><body><div class=\"wrap\">");
        b.AppendFormat("<h1><span class=\"dot\"></span>SpaceScan report</h1><div class=\"sub\">{0} &middot; {1:yyyy-MM-dd HH:mm}</div>",
            Esc(root.FullPath), DateTime.Now);
        b.AppendFormat("<div class=\"cards\"><div class=\"card\"><span class=\"eyebrow\">Total size</span><b>{0}</b></div>" +
                       "<div class=\"card\"><span class=\"eyebrow\">Files</span><b>{1:N0}</b></div>" +
                       "<div class=\"card\"><span class=\"eyebrow\">Folders</span><b>{2:N0}</b></div>" +
                       "<div class=\"card\"><span class=\"eyebrow\">File types</span><b>{3:N0}</b></div></div>",
            Fmt.Size(root.Size), root.Files, dirs.Count, ext.Count);

        Table(b, "Largest folders", "Folder", dirs.OrderByDescending(n => n.Size).Take(50)
            .Select(n => new[] { Esc(n.FullPath), Bar(n.Size, root.Size), Fmt.Size(n.Size), n.Files.ToString("N0") }), "Size", "Files");

        Table(b, "Categories", "Category", Categories.Group(ext).Take(20)
            .Select(kv => new[] { Esc(kv.Key), Bar(kv.Value[0], root.Size), Fmt.Size(kv.Value[0]), kv.Value[1].ToString("N0") }), "Size", "Files");

        Table(b, "File types", "Extension", ext.Where(kv => kv.Value[1] > 0).OrderByDescending(kv => kv.Value[0]).Take(25)
            .Select(kv => new[] { Esc(kv.Key), Bar(kv.Value[0], root.Size), Fmt.Size(kv.Value[0]), kv.Value[1].ToString("N0") }), "Size", "Files");

        Table(b, "Largest files", "File", top.Take(50)
            .Select(f => new[] { Esc(f.Name), Esc(f.Dir.FullPath), Fmt.Size(f.Size), "" }), "Folder", "Size");

        if (changes != null && changes.Count > 0)
            Table(b, "Changes since the previous scan", "Folder", changes.Take(50)
                .Select(c => new[] { Esc(c.Path), Fmt.Size(c.Before), Fmt.Size(c.After),
                    "<span class=\"" + (c.Delta > 0 ? "up" : "down") + "\">" + (c.Delta > 0 ? "+" : "-") + Fmt.Size(Math.Abs(c.Delta)) + "</span>" }),
                "Before / now", "Change");

        b.Append("</div></body></html>");
        return b.ToString();
    }

    static string Bar(long part, long total)
    {
        return string.Format(CultureInfo.InvariantCulture, "<div class=\"track\"><div class=\"bar\" style=\"width:{0:0.#}%\"></div></div>",
            total > 0 ? 100.0 * part / total : 0);
    }

    static void Table(StringBuilder b, string title, string first, IEnumerable<string[]> rows, string third, string fourth)
    {
        b.AppendFormat("<section><span class=\"eyebrow\">{0}</span><table><tr><th>{1}</th><th></th><th>{2}</th><th>{3}</th></tr>", title, first, third, fourth);
        foreach (var r in rows)
            b.AppendFormat("<tr><td>{0}</td><td class=\"barcell\">{1}</td><td class=\"n\">{2}</td><td class=\"n\">{3}</td></tr>", r[0], r[1], r[2], r[3]);
        b.Append("</table></section>");
    }

    public static string Esc(string v)
    {
        return (v ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    public static IEnumerable<string> FolderCsv(Node root)
    {
        yield return "Path;Size (bytes);Size;Files;Level";
        var stack = new Stack<KeyValuePair<Node, int>>();
        stack.Push(new KeyValuePair<Node, int>(root, 0));
        while (stack.Count > 0)
        {
            var kv = stack.Pop();
            var n = kv.Key;
            yield return string.Format("\"{0}\";{1};{2};{3};{4}", n.FullPath.Replace("\"", "\"\""), n.Size, Fmt.Size(n.Size), n.Files, kv.Value);
            for (int i = n.Dirs.Count - 1; i >= 0; i--) stack.Push(new KeyValuePair<Node, int>(n.Dirs[i], kv.Value + 1));
        }
    }
}

// Headless mode: SpaceScan.exe <path> [--report file.html] [--csv file.csv] [--snapshot file.scan] [--compare file.scan] [--exclude rule;rule]
static class Cli
{
    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int pid);

    public static int Run(string[] args)
    {
        AttachConsole(-1); // write into the console that launched us, if there is one
        string path = null, html = null, csv = null, snapshot = null, compare = null;
        bool auto = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string next = i + 1 < args.Length ? args[i + 1] : null;
            if (a == "--report" || a == "-r") html = Next(args, ref i);
            else if (a == "--csv") csv = Next(args, ref i);
            else if (a == "--snapshot") snapshot = Next(args, ref i);
            else if (a == "--compare") compare = Next(args, ref i);
            else if (a == "--compare-last") auto = true;
            else if (a == "--exclude") Exclude.Set((Next(args, ref i) ?? "").Split(';'));
            else if (a == "--help" || a == "-h" || a == "/?") { Console.WriteLine(Usage); return 0; }
            else if (!a.StartsWith("-")) path = a;
        }
        if (path == null) { Console.WriteLine(Usage); return 2; }
        if (path.Length == 2 && path[1] == ':') path += "\\";
        if (!Directory.Exists(path)) { Console.Error.WriteLine("Folder not found: " + path); return 2; }

        var started = DateTime.Now;
        Node root = null;
        string mode = "classic";
        try
        {
            if (Mft.IsNtfsRoot(path)) { root = Mft.Scan(path); mode = "MFT"; }
        }
        catch (Exception ex) { Console.Error.WriteLine("MFT unavailable (" + ex.Message + "), walking folders instead"); }
        if (root == null)
        {
            Stats.Reset();
            root = new Node { Name = path };
            Scanner.Scan(root, path);
            Stats.Trim();
        }
        Console.WriteLine("{0}: {1} in {2:N0} files, {3:0.0}s, {4} scan", path, Fmt.Size(root.Size), root.Files, (DateTime.Now - started).TotalSeconds, mode);

        List<Change> changes = null;
        string against = compare;
        if (against == null && auto) against = History.List(root.FullPath).FirstOrDefault();
        if (against != null)
        {
            try
            {
                DateTime taken;
                string snapRoot;
                changes = Comparer.Run(Snapshot.Load(against, out taken, out snapRoot), root, 5000);
                Console.WriteLine("compared against {0} ({1:yyyy-MM-dd HH:mm}): {2:N0} folders changed", Path.GetFileName(against), taken, changes.Count);
            }
            catch (Exception ex) { Console.Error.WriteLine("compare failed: " + ex.Message); }
        }

        try
        {
            if (html != null) { File.WriteAllText(html, Report.Html(root, Stats.Ext, Stats.Top, changes), new UTF8Encoding(true)); Console.WriteLine("report: " + html); }
            if (csv != null) { File.WriteAllLines(csv, Report.FolderCsv(root), new UTF8Encoding(true)); Console.WriteLine("csv: " + csv); }
            if (snapshot != null) { Snapshot.Save(root, snapshot); Console.WriteLine("snapshot: " + snapshot); }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        History.Save(root);
        return 0;
    }

    static string Next(string[] args, ref int i) { return ++i < args.Length ? args[i] : null; }

    const string Usage =
        "SpaceScan <folder or drive> [options]\n" +
        "  --report <file.html>   write an HTML report\n" +
        "  --csv <file.csv>       write every folder to CSV\n" +
        "  --snapshot <file.scan> save a snapshot for later comparison\n" +
        "  --compare <file.scan>  compare against that snapshot\n" +
        "  --compare-last         compare against the newest automatic snapshot of this drive\n" +
        "  --exclude \"a;b\"        exclusion rules, separated by ;\n" +
        "Runs without a window. Scanning a whole NTFS drive needs administrator rights.";
}
