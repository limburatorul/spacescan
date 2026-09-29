using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("SpaceScan")]
[assembly: System.Reflection.AssemblyProduct("SpaceScan")]
[assembly: System.Reflection.AssemblyCompany("Protagonist Labs")]
[assembly: System.Reflection.AssemblyCopyright("MIT licence")]
[assembly: System.Reflection.AssemblyVersion("1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0")]

static class Brand
{
    public static readonly Brush Text = Make("#e8eef6"), Dim = Make("#8a97aa"), Dimmer = Make("#5d6879");
    public static readonly Brush Accent = Make("#4c8dff"), Pink = Make("#ff5c8a"), Ground = Make("#0a0d13");

    static Brush Make(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
        b.Freeze();
        return b;
    }

    public static string Fmt(long b) { return global::Fmt.Size(b); }

    public static string Pct(long part, long total) { return global::Fmt.Pct(part, total); }
}

// One visible line of the folder tree. The tree is shown flattened in a virtualized list, so columns line up at any depth.
public class Row : INotifyPropertyChanged
{
    const string FolderIcon = "M0,1.5 L5.5,1.5 L7,3 L14,3 L14,12 L0,12 Z", FileIcon = "M1,0 L8,0 L11,3 L11,13 L1,13 Z";
    internal Node Node;      // folder row
    internal FileEntry File; // file row
    internal int Level;
    bool expanded;

    public event PropertyChangedEventHandler PropertyChanged;
    public void Changed() { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(string.Empty)); }

    internal object Tag { get { return Node != null ? (object)Node : File; } }
    internal bool IsExpanded { get { return expanded; } set { expanded = value; Changed(); } }
    internal bool CanExpand { get { return Node != null && (Node.Files > 0 || Node.Dirs.Count > 0); } }
    long Size { get { return Node != null ? Node.Size : File.Size; } }
    long ParentSize { get { var p = Node != null ? Node.Parent : File.Dir; return p != null ? p.Size : Size; } }
    double Fraction { get { return ParentSize > 0 ? (double)Size / ParentSize : 0; } }

    public string Chevron { get { return !CanExpand ? "" : expanded ? "▾" : "▸"; } }
    public Thickness Indent { get { return new Thickness(Level * 18, 0, 0, 0); } }
    public string IconData { get { return Node != null ? FolderIcon : File.Summary ? "M0,0" : FileIcon; } }
    public Brush IconBrush { get { return Node != null ? Brand.Accent : Brand.Dimmer; } }
    public string DisplayName { get { return Node != null ? Node.Name + (Node.Denied ? "   (access denied)" : "") : File.Name; } }
    public Brush NameBrush { get { return Node != null ? (Node.Denied ? Brand.Pink : Brand.Text) : File.Summary ? Brand.Dimmer : Brand.Dim; } }
    public double BarWidth { get { return Math.Max(Size > 0 ? 1 : 0, Fraction * 150); } }
    public Brush BarBrush { get { return Node != null ? Brand.Accent : Brand.Dim; } }
    public string SizeText { get { return Brand.Fmt(Size); } }
    public string PctText { get { return (Fraction * 100).ToString("0.0") + " %"; } }
    public string FilesText { get { return Node != null ? Node.Files.ToString("N0") : ""; } }
}

public class ExtRow
{
    internal long SizeValue, CountValue, AvgValue;
    public string Ext { get; set; }
    public string SizeText { get; set; }
    public string PctText { get; set; }
    public string CountText { get; set; }
    public string AvgText { get; set; }
}

public class TopRow
{
    internal FileEntry File;
    internal long SizeValue;
    public string Name { get; set; }
    public string SizeText { get; set; }
    public string PctText { get; set; }
    public string Folder { get; set; }
}

// A list whose column headers sort it: click to sort, click again to flip.
class SortableList<T>
{
    readonly ListBox list;
    readonly Dictionary<string, Func<T, object>> keys;
    readonly Dictionary<string, Button> headers = new Dictionary<string, Button>();
    readonly Dictionary<string, string> labels = new Dictionary<string, string>();
    List<T> items = new List<T>();
    string key;
    bool asc;

    public SortableList(ListBox list, string initial, Dictionary<string, Func<T, object>> keys, Dictionary<string, Button> buttons)
    {
        this.list = list;
        this.keys = keys;
        this.key = initial;
        foreach (var kv in buttons)
        {
            string k = kv.Key;
            headers[k] = kv.Value;
            labels[k] = (string)kv.Value.Content;
            kv.Value.Click += delegate { asc = key == k && !asc; key = k; Apply(); };
        }
    }

    public List<T> Items { get { return items; } }
    public void Set(IEnumerable<T> source) { items = source.ToList(); Apply(); }
    public void Clear() { items = new List<T>(); Apply(); }
    public void Remove(Predicate<T> gone) { items.RemoveAll(gone); Apply(); }
    public void Label(string column, string text) { labels[column] = text; Apply(); }

    void Apply()
    {
        var sorted = items.OrderBy(x => keys[key](x)).ToList();
        if (!asc) sorted.Reverse();
        list.ItemsSource = sorted;
        foreach (var kv in headers) kv.Value.Content = labels[kv.Key] + (kv.Key == key ? (asc ? "  ↑" : "  ↓") : "");
    }
}

public class DriveRow
{
    internal string Path;
    public string Name { get; set; }
    public string Kind { get; set; }
    public string UsedText { get; set; }
    public string TotalText { get; set; }
    public string FreeText { get; set; }
    public double BarWidth { get; set; }
    public Brush BarBrush { get; set; }
}

public class FoundRow
{
    internal FileEntry File;
    internal long SizeValue;
    internal long DateValue;
    public string Name { get; set; }
    public string SizeText { get; set; }
    public string ModifiedText { get; set; }
    public string Folder { get; set; }
}

// Either a group header (File == null) or one copy inside the group above it.
public class DupRow
{
    internal FileEntry File;
    public string Name { get; set; }
    public string SizeText { get; set; }
    public string Info { get; set; }
    public Thickness Indent { get { return new Thickness(File == null ? 0 : 26, 0, 0, 0); } }
    public FontWeight Weight { get { return File == null ? FontWeights.SemiBold : FontWeights.Normal; } }
    public Brush NameBrush { get { return File == null ? Brand.Text : Brand.Dim; } }
    public Brush InfoBrush { get { return File == null ? Brand.Accent : Brand.Dimmer; } }
}

public class CmpRow
{
    internal long BeforeValue, AfterValue, DeltaValue;
    public string Path { get; set; }
    public string BeforeText { get; set; }
    public string AfterText { get; set; }
    public string DeltaText { get; set; }
    public Brush DeltaBrush { get; set; }
}

class MainUI
{
    const int MaxFilesShown = 1000;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")]
    static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);
    struct MARGINS { public int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string lpVerb, lpFile, lpParameters, lpDirectory;
        public int nShow;
        public IntPtr hInstApp, lpIDList;
        public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon, hProcess;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    public readonly Window Window;
    readonly TextBox pathBox;
    readonly Button scanBtn, browseBtn, exportBtn;
    readonly RadioButton tabTree, tabExt, tabTop;
    readonly Grid viewTree, viewExt, viewTop;
    readonly ListBox treeList, extList, topList, foundList, dupList;
    readonly TextBox findName, findSize, findAge, dupSize;
    readonly TextBlock dupSummary, findHint, dupHint;
    readonly RadioButton tabSearch, tabDup, tabCmp;
    readonly Grid viewSearch, viewDup, viewCmp;
    readonly ListBox cmpList;
    readonly TextBlock cmpSummary, cmpHint;
    readonly TextBox excludeBox;
    readonly System.Windows.Controls.Primitives.Popup excludePopup;

    readonly SortableList<ExtRow> extSort;
    readonly SortableList<TopRow> topSort;
    readonly SortableList<FoundRow> findSort;
    readonly SortableList<CmpRow> cmpSort;
    readonly ListBox driveList;
    readonly RadioButton tabDrives, byCat;
    readonly Grid viewDrives;
    List<Change> lastDiff;
    string ownHistoryFile;
    readonly ObservableCollection<DupRow> dups = new ObservableCollection<DupRow>();
    readonly Dictionary<string, Node> dirCache = new Dictionary<string, Node>();
    int phase; // 0 = scan, 1 = search, 2 = duplicates
    readonly TextBlock status;
    readonly Border progress;
    readonly ContextMenu menu;
    readonly FrameworkElement emptyState;
    readonly ObservableCollection<Row> rows = new ObservableCollection<Row>();
    readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
    readonly Stopwatch sw = new Stopwatch();
    bool busy;
    Node root;
    static string startPath;

    T Find<T>(string name) { return (T)Window.FindName(name); }

    public MainUI()
    {
        using (var s = typeof(MainUI).Assembly.GetManifestResourceStream("MainWindow.xaml")) Window = (Window)XamlReader.Load(s);
        pathBox = Find<TextBox>("pathBox");
        scanBtn = Find<Button>("scanBtn");
        browseBtn = Find<Button>("browseBtn");
        exportBtn = Find<Button>("exportBtn");
        tabTree = Find<RadioButton>("tabTree");
        tabExt = Find<RadioButton>("tabExt");
        tabTop = Find<RadioButton>("tabTop");
        viewTree = Find<Grid>("viewTree");
        viewExt = Find<Grid>("viewExt");
        viewTop = Find<Grid>("viewTop");
        treeList = Find<ListBox>("treeList");
        extList = Find<ListBox>("extList");
        topList = Find<ListBox>("topList");
        foundList = Find<ListBox>("foundList");
        dupList = Find<ListBox>("dupList");
        findName = Find<TextBox>("findName");
        findSize = Find<TextBox>("findSize");
        findAge = Find<TextBox>("findAge");
        dupSize = Find<TextBox>("dupSize");
        dupSummary = Find<TextBlock>("dupSummary");
        findHint = Find<TextBlock>("findHint");
        dupHint = Find<TextBlock>("dupHint");
        tabSearch = Find<RadioButton>("tabSearch");
        tabDup = Find<RadioButton>("tabDup");
        viewSearch = Find<Grid>("viewSearch");
        viewDup = Find<Grid>("viewDup");
        tabCmp = Find<RadioButton>("tabCmp");
        viewCmp = Find<Grid>("viewCmp");
        cmpList = Find<ListBox>("cmpList");
        cmpSummary = Find<TextBlock>("cmpSummary");
        cmpHint = Find<TextBlock>("cmpHint");
        excludeBox = Find<TextBox>("excludeBox");
        excludePopup = Find<System.Windows.Controls.Primitives.Popup>("excludePopup");

        dupList.ItemsSource = dups;
        driveList = Find<ListBox>("driveList");
        tabDrives = Find<RadioButton>("tabDrives");
        viewDrives = Find<Grid>("viewDrives");
        byCat = Find<RadioButton>("byCat");

        extSort = new SortableList<ExtRow>(extList, "size",
            new Dictionary<string, Func<ExtRow, object>> { { "name", r => r.Ext }, { "size", r => r.SizeValue }, { "files", r => r.CountValue }, { "avg", r => r.AvgValue } },
            new Dictionary<string, Button> { { "name", Find<Button>("extHdrName") }, { "size", Find<Button>("extHdrSize") },
                { "files", Find<Button>("extHdrFiles") }, { "avg", Find<Button>("extHdrAvg") } });
        topSort = new SortableList<TopRow>(topList, "size",
            new Dictionary<string, Func<TopRow, object>> { { "name", r => r.Name }, { "size", r => r.SizeValue }, { "folder", r => r.Folder } },
            new Dictionary<string, Button> { { "name", Find<Button>("topHdrName") }, { "size", Find<Button>("topHdrSize") }, { "folder", Find<Button>("topHdrFolder") } });
        findSort = new SortableList<FoundRow>(foundList, "size",
            new Dictionary<string, Func<FoundRow, object>> { { "name", r => r.Name }, { "size", r => r.SizeValue }, { "date", r => r.DateValue }, { "folder", r => r.Folder } },
            new Dictionary<string, Button> { { "name", Find<Button>("findHdrName") }, { "size", Find<Button>("findHdrSize") },
                { "date", Find<Button>("findHdrDate") }, { "folder", Find<Button>("findHdrFolder") } });
        cmpSort = new SortableList<CmpRow>(cmpList, "delta",
            new Dictionary<string, Func<CmpRow, object>> { { "path", r => r.Path }, { "before", r => r.BeforeValue }, { "after", r => r.AfterValue }, { "delta", r => Math.Abs(r.DeltaValue) } },
            new Dictionary<string, Button> { { "path", Find<Button>("cmpHdrPath") }, { "before", Find<Button>("cmpHdrBefore") },
                { "after", Find<Button>("cmpHdrAfter") }, { "delta", Find<Button>("cmpHdrDelta") } });
        status = Find<TextBlock>("status");
        progress = Find<Border>("progress");
        menu = (ContextMenu)Window.Resources["RowMenu"];

        Window.SourceInitialized += delegate { ApplyGlass(); };
        treeList.ItemsSource = rows;

        var drivesBtn = Find<Button>("drivesBtn");
        drivesBtn.Click += delegate { ShowDrives(drivesBtn); };
        var first = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady);
        if (first != null) pathBox.Text = first.Name;
        emptyState = Find<FrameworkElement>("emptyState");
        // With the content running under the title bar, a maximized window overhangs the screen by the frame width.
        var maxBtn = Find<Button>("maxBtn");
        Window.StateChanged += delegate
        {
            bool max = Window.WindowState == WindowState.Maximized;
            Window.BorderThickness = new Thickness(max ? 8 : 0);
            maxBtn.Content = max ? "" : ""; // restore / maximize glyphs
        };
        Find<Button>("minBtn").Click += delegate { SystemCommands.MinimizeWindow(Window); };
        maxBtn.Click += delegate { if (Window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(Window); else SystemCommands.MaximizeWindow(Window); };
        Find<Button>("closeBtn").Click += delegate { Window.Close(); };

        scanBtn.Click += delegate { StartOrStop(); };
        pathBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; StartOrStop(); } };
        browseBtn.Click += delegate
        {
            using (var dlg = new WinForms.FolderBrowserDialog { SelectedPath = pathBox.Text })
                if (dlg.ShowDialog() == WinForms.DialogResult.OK) pathBox.Text = dlg.SelectedPath;
        };
        exportBtn.Click += delegate { ShowExportMenu(exportBtn); };
        tabTree.Checked += delegate { SetView(0); };
        tabExt.Checked += delegate { SetView(1); };
        tabTop.Checked += delegate { SetView(2); };
        tabSearch.Checked += delegate { SetView(3); };
        tabDup.Checked += delegate { SetView(4); };
        tabCmp.Checked += delegate { SetView(5); };
        tabDrives.Checked += delegate { SetView(6); RefreshDrives(); };
        byCat.Checked += delegate { RefreshLists(); };
        Find<RadioButton>("byExt").Checked += delegate { RefreshLists(); };
        Find<Button>("scanAllBtn").Click += delegate { ScanAllDrives(); };
        driveList.MouseDoubleClick += delegate
        {
            var d = driveList.SelectedItem as DriveRow;
            if (d == null) return;
            pathBox.Text = d.Path;
            tabTree.IsChecked = true;
            StartOrStop();
        };
        Find<Button>("cmpPrevBtn").Click += delegate { ComparePrevious(); };
        Find<Button>("cmpHistoryBtn").Click += delegate { ShowHistoryMenu((Button)Window.FindName("cmpHistoryBtn")); };
        Find<Button>("saveSnapBtn").Click += delegate { SaveSnapshot(); };
        Find<Button>("cmpBtn").Click += delegate { CompareWithSnapshot(); };
        var excludeBtn = Find<Button>("excludeBtn");
        excludeBtn.Click += delegate
        {
            excludePopup.PlacementTarget = excludeBtn;
            excludePopup.IsOpen = true;
            excludeBox.Focus();
        };
        Find<Button>("excludeApply").Click += delegate { SaveExclusions(); };
        Find<Button>("shellBtn").Click += delegate { SetShellMenu(!ShellMenuInstalled); RefreshIntegration(); };
        Find<Button>("fileLabsBtn").Click += delegate
        {
            string exe = FileLabsExe;
            Launch(exe ?? FileLabsPage, exe != null && root != null ? root.FullPath : null);
        };
        excludePopup.Opened += delegate { RefreshIntegration(); };
        LoadExclusions();
        RegisterSelf();
        Find<Button>("findBtn").Click += delegate { RunSearch(); };
        Find<Button>("dupBtn").Click += delegate { RunDupes(); };
        foreach (var box in new[] { findName, findSize, findAge })
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; RunSearch(); } };
        dupSize.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; RunDupes(); } };
        Window.PreviewKeyDown += (s, e) =>
        {
            if (e.Key != Key.F || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            tabSearch.IsChecked = true;
            findName.Focus();
            findName.SelectAll();
            e.Handled = true;
        };
        foundList.MouseDoubleClick += delegate { var f = foundList.SelectedItem as FoundRow; if (f != null) RevealFile(f.File); };
        dupList.MouseDoubleClick += delegate { var d = dupList.SelectedItem as DupRow; if (d != null && d.File != null) RevealFile(d.File); };
        foreach (var list in new[] { foundList, dupList })
            list.PreviewKeyDown += (s, e) => HandleKey(e, MenuTags());
        timer.Tick += delegate { ShowProgress(); };

        treeList.PreviewMouseLeftButtonDown += (s, e) =>
        {
            var r = RowAt(e.OriginalSource as DependencyObject, "chev");
            if (r != null) { Toggle(r); e.Handled = true; }
        };
        treeList.MouseDoubleClick += (s, e) =>
        {
            var r = RowAt(e.OriginalSource as DependencyObject, null);
            if (r == null) return;
            if (r.Node != null) Toggle(r); else Open(r.File);
        };
        treeList.PreviewKeyDown += (s, e) => TreeKey(e);
        topList.MouseDoubleClick += delegate { var t = topList.SelectedItem as TopRow; if (t != null) RevealFile(t.File); };
        topList.PreviewKeyDown += (s, e) => HandleKey(e, MenuTags());

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            string action = (string)item.Tag;
            item.Click += delegate { MenuAction(action, MenuTags()); };
        }
        treeList.ContextMenu = menu;
        topList.ContextMenu = menu;
        foundList.ContextMenu = menu;
        dupList.ContextMenu = menu;
        ContextMenuEventHandler opening = (s, e) =>
        {
            var tags = MenuTags();
            if (tags.Count == 0) { e.Handled = true; return; }
            foreach (var item in menu.Items.OfType<MenuItem>()) item.IsEnabled = !busy;
        };
        foreach (var list in new[] { treeList, topList, foundList, dupList }) list.ContextMenuOpening += opening;

        Find<Button>("elevateBtn").Click += delegate { RestartElevated(); };
        status.Text = "Ready";
        if (startPath != null)
        {
            pathBox.Text = startPath;
            Window.ContentRendered += delegate { StartOrStop(); };
        }
    }

    // Reading the MFT needs admin; the app itself runs as the plain user so the command line stays unattended.
    static bool IsAdmin
    {
        get
        {
            using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    void RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, Verb = "runas" });
            Window.Close();
        }
        catch (Exception ex) { Error(ex.Message); } // cancelled UAC lands here
    }

    // Drive picker: same opaque menu look as the row menu, one line per ready drive with its free space.
    void ShowDrives(Button anchor)
    {
        var m = new ContextMenu { Template = menu.Template, Foreground = menu.Foreground, FontSize = menu.FontSize, HasDropShadow = false,
            PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, VerticalOffset = 6 };
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            string label;
            try { label = string.Format("{0} free of {1}", Brand.Fmt(d.TotalFreeSpace), Brand.Fmt(d.TotalSize)); }
            catch (IOException) { label = ""; }
            var item = new MenuItem { Style = (Style)Window.Resources["MI"], Tag = d.Name, InputGestureText = label,
                Header = d.Name.TrimEnd('\\') + (string.IsNullOrEmpty(d.VolumeLabel) ? "" : "   " + d.VolumeLabel) };
            item.Click += (s, e) => { pathBox.Text = (string)((MenuItem)s).Tag; StartOrStop(); };
            m.Items.Add(item);
        }
        m.IsOpen = true;
    }

    // Acrylic through DWM (Win11 22H2+). WPF composes with alpha, so the desktop shows through the translucent layers.
    // Anywhere it isn't available (older Windows, Windows Server 2019/2022) the window keeps its opaque ground.
    void ApplyGlass()
    {
        var h = new WindowInteropHelper(Window).Handle;
        HwndSource.FromHwnd(h).CompositionTarget.BackgroundColor = Colors.Transparent; // default is opaque black: partial alpha would go dark
        Dwm(h, 20, 1);          // dark title bar
        Dwm(h, 33, 2);          // rounded corners
        Dwm(h, 34, 0x00362B26); // border color #262b36 (0x00BBGGRR)
        Dwm(h, 36, 0x00F6EEE8); // caption text #e8eef6
        var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        bool glass = DwmExtendFrameIntoClientArea(h, ref m) == 0 && Dwm(h, 38, 3 /*acrylic*/) == 0;
        Window.Background = glass ? Brushes.Transparent : Brand.Ground;
    }

    static int Dwm(IntPtr h, int attr, int value) { return DwmSetWindowAttribute(h, attr, ref value, 4); }

    void Error(string msg) { MessageBox.Show(Window, msg, "SpaceScan", MessageBoxButton.OK, MessageBoxImage.Error); }

    void SetView(int v)
    {
        var views = new[] { viewTree, viewExt, viewTop, viewSearch, viewDup, viewCmp, viewDrives };
        for (int i = 0; i < views.Length; i++) views[i].Visibility = i == v ? Visibility.Visible : Visibility.Collapsed;
    }

    // "100 MB", "1.5G", "500000" -> bytes.
    static long ParseSize(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match((text ?? "").Trim().ToUpperInvariant(), @"^([\d.,]+)\s*([KMGT]?)B?$");
        double v;
        if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return 0;
        int p = "_KMGT".IndexOf(m.Groups[2].Value.Length == 0 ? '_' : m.Groups[2].Value[0]);
        return (long)(v * Math.Pow(1024, Math.Max(0, p)));
    }

    static string ModifiedText(FileEntry f)
    {
        return f.Modified > 0 ? DateTime.FromFileTimeUtc(f.Modified).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
    }

    void StartJob(int kind, Action work)
    {
        phase = kind;
        Scanner.Cancel = false;
        Scanner.FilesSeen = 0;
        Scanner.Total = 0;
        SetBusy(true);
        new Thread(() => work(), 256 * 1024 * 1024) { IsBackground = true }.Start();
    }

    void EndJob(string text)
    {
        phase = 0;
        SetBusy(false);
        status.Text = text;
    }

    // Files found by an MFT pass point at a throwaway tree; re-point them at the tree on screen so deletes update it.
    void ResolveDirs(IEnumerable<FileEntry> files)
    {
        foreach (var f in files)
        {
            if (f.Dir == null) continue;
            string path = f.Dir.FullPath;
            Node n;
            if (!dirCache.TryGetValue(path, out n)) dirCache[path] = n = FindNodeByPath(path, true);
            if (n != null) f.Dir = n;
        }
    }

    static string ExePath
    {
        get
        {
            string exe = typeof(MainUI).Assembly.Location; // the exe itself, also when another process hosts us
            return string.IsNullOrEmpty(exe) ? Process.GetCurrentProcess().MainModule.FileName : exe;
        }
    }

    const string FileLabsPage = "https://protagonistlabs.app/filelabs/?utm_source=spacescan&utm_medium=app&utm_campaign=open-in-filelabs";

    static string FileLabsExe
    {
        get
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "File Labs", "FileLabs.exe");
            return File.Exists(p) ? p : null;
        }
    }

    // Publishes where we live, so File Labs (or anything else) can offer "Scan with SpaceScan".
    static void RegisterSelf()
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\SpaceScan"))
                if (k != null) k.SetValue("ExePath", ExePath);
        }
        catch (Exception) { }
    }

    static readonly string[] ShellKeys =
    {
        @"Software\Classes\Directory\shell\SpaceScan",
        @"Software\Classes\Drive\shell\SpaceScan",
        @"Software\Classes\Directory\Background\shell\SpaceScan"
    };

    static bool ShellMenuInstalled
    {
        get { using (var k = Registry.CurrentUser.OpenSubKey(ShellKeys[0])) return k != null; }
    }

    void SetShellMenu(bool on)
    {
        try
        {
            foreach (var key in ShellKeys)
            {
                if (!on) { Registry.CurrentUser.DeleteSubKeyTree(key, false); continue; }
                using (var k = Registry.CurrentUser.CreateSubKey(key))
                {
                    k.SetValue("", "Scan with SpaceScan");
                    k.SetValue("Icon", ExePath);
                }
                using (var c = Registry.CurrentUser.CreateSubKey(key + @"\command"))
                    c.SetValue("", "\"" + ExePath + "\" \"" + (key.Contains("Background") ? "%V" : "%1") + "\"");
            }
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    void RefreshIntegration()
    {
        Find<Button>("shellBtn").Content = ShellMenuInstalled ? "Remove" : "Add";
        string exe = FileLabsExe;
        Find<TextBlock>("fileLabsText").Text = exe != null
            ? "File Labs is installed — the row menu can open a folder in it"
            : "File Labs is not installed — the menu item opens its page";
        Find<Button>("fileLabsBtn").Content = exe != null ? "Open" : "Get it";
    }

    void Launch(string what, string argument)
    {
        try
        {
            var psi = argument == null ? new ProcessStartInfo(what) { UseShellExecute = true } : new ProcessStartInfo(what, "\"" + argument + "\"");
            Process.Start(psi);
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    void OpenInFileLabs(object tag)
    {
        string p = PathOf(tag);
        if (p == null) return;
        string exe = FileLabsExe;
        if (exe == null) Launch(FileLabsPage, null);
        else Launch(exe, tag is Node ? p : Path.GetDirectoryName(p));
    }

    static string ExcludeFile
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceScan", "exclude.txt"); }
    }

    void LoadExclusions()
    {
        try
        {
            if (!File.Exists(ExcludeFile)) return;
            excludeBox.Text = File.ReadAllText(ExcludeFile);
            Exclude.Set(excludeBox.Text.Split('\n'));
        }
        catch (IOException) { } // no rules is a fine fallback
    }

    void SaveExclusions()
    {
        Exclude.Set(excludeBox.Text.Split('\n'));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ExcludeFile));
            File.WriteAllText(ExcludeFile, excludeBox.Text);
        }
        catch (Exception ex) { Error(ex.Message); }
        excludePopup.IsOpen = false;
        status.Text = Exclude.Patterns.Length == 0
            ? "No exclusions. Everything gets scanned."
            : string.Format("{0} exclusion rule(s) saved — they apply to the next scan: {1}", Exclude.Patterns.Length, string.Join(", ", Exclude.Patterns));
    }

    // --- snapshots and comparison ---

    void SaveSnapshot()
    {
        if (root == null || busy) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "SpaceScan snapshot (*.scan)|*.scan",
            FileName = root.Name.Replace(":", "").Replace("\\", "-").Trim('-') + "-" + DateTime.Now.ToString("yyyy-MM-dd") + ".scan" };
        if (dlg.ShowDialog(Window) != true) return;
        SaveSnapshotTo(dlg.FileName);
    }

    void SaveSnapshotTo(string file)
    {
        var saved = root;
        StartJob(3, () =>
        {
            string message;
            try
            {
                Snapshot.Save(saved, file);
                message = string.Format("Snapshot saved: {0}  •  {1}", file, Brand.Fmt(new FileInfo(file).Length));
            }
            catch (Exception ex) { message = "Snapshot failed: " + ex.Message; }
            Window.Dispatcher.BeginInvoke(new Action(() => EndJob(message)));
        });
    }

    void CompareWithSnapshot()
    {
        if (root == null || busy) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "SpaceScan snapshot (*.scan)|*.scan" };
        if (dlg.ShowDialog(Window) == true) CompareWith(dlg.FileName);
    }

    void CompareWith(string file)
    {
        if (root == null || busy) return;
        var now = root;
        cmpSort.Clear();
        cmpHint.Visibility = Visibility.Collapsed;
        StartJob(3, () =>
        {
            DateTime taken;
            string snapRoot;
            List<Change> diff;
            try
            {
                var before = Snapshot.Load(file, out taken, out snapRoot);
                diff = Comparer.Run(before, now, 20000);
            }
            catch (Exception ex)
            {
                Window.Dispatcher.BeginInvoke(new Action(() => { EndJob("Compare failed: " + ex.Message); cmpHint.Visibility = Visibility.Visible; }));
                return;
            }
            Window.Dispatcher.BeginInvoke(new Action(() =>
            {
                lastDiff = diff;
                cmpSort.Set(diff.Select(c => new CmpRow { Path = c.Path, BeforeValue = c.Before, AfterValue = c.After, DeltaValue = c.Delta,
                    BeforeText = Brand.Fmt(c.Before), AfterText = Brand.Fmt(c.After),
                    DeltaText = (c.Delta > 0 ? "+" : "-") + Brand.Fmt(Math.Abs(c.Delta)), DeltaBrush = c.Delta > 0 ? Brand.Pink : Brand.Accent }));
                long total = now.Size - diff.Where(c => c.Path == now.Name).Select(c => c.Before).DefaultIfEmpty(now.Size).First();
                cmpSummary.Text = string.Format("{0} taken {1:yyyy-MM-dd HH:mm}{2}  •  {3:N0} folders changed  •  total {4}{5}",
                    Path.GetFileName(file), taken,
                    snapRoot.Equals(now.Name, StringComparison.OrdinalIgnoreCase) ? "" : "  •  WARNING: snapshot is of " + snapRoot,
                    diff.Count, total >= 0 ? "+" : "-", Brand.Fmt(Math.Abs(total)));
                cmpHint.Text = "No differences found.";
                cmpHint.Visibility = diff.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                EndJob(string.Format("Compared against {0}  •  {1:N0} folders changed  •  {2:0.0}s", Path.GetFileName(file), diff.Count, sw.Elapsed.TotalSeconds));
            }));
        });
    }

    void RefreshDrives()
    {
        var list = new List<DriveRow>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady) continue;
            long total = 0, free = 0;
            string kind;
            try
            {
                total = d.TotalSize;
                free = d.TotalFreeSpace;
                kind = string.Join(" · ", new[] { d.DriveType.ToString(), d.DriveFormat, d.VolumeLabel }.Where(x => !string.IsNullOrEmpty(x)));
            }
            catch (IOException) { continue; }
            long used = total - free;
            list.Add(new DriveRow { Path = d.Name, Name = d.Name.TrimEnd('\\'), Kind = kind,
                UsedText = Brand.Fmt(used), TotalText = Brand.Fmt(total), FreeText = Brand.Fmt(free),
                BarWidth = total > 0 ? Math.Max(1, 180.0 * used / total) : 0,
                BarBrush = total > 0 && free < total * 0.1 ? Brand.Pink : Brand.Accent });
        }
        driveList.ItemsSource = list;
    }

    // Every fixed drive in one tree, each root kept under a virtual "This PC" node.
    void ScanAllDrives()
    {
        if (busy) return;
        var drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Select(d => d.Name).ToList();
        if (drives.Count == 0) { Error("No local drives found."); return; }
        Stats.Reset();
        Scanner.DeniedCount = 0;
        root = null;
        rows.Clear();
        findSort.Clear();
        dups.Clear();
        cmpSort.Clear();
        dirCache.Clear();
        emptyState.Visibility = Visibility.Collapsed;
        tabTree.IsChecked = true;
        StartJob(0, () =>
        {
            var all = new Node { Name = "This PC" };
            foreach (var d in drives)
            {
                if (Scanner.Cancel) break;
                Node r = null;
                try { if (Mft.IsNtfsRoot(d)) r = Mft.Scan(d); }
                catch (Exception) { }
                if (r == null)
                {
                    Scanner.Total = 0;
                    r = new Node { Name = d };
                    Scanner.Scan(r, d);
                }
                r.Parent = all;
                all.Dirs.Add(r);
                all.Size += r.Size;
                all.Files += r.Files;
            }
            all.Dirs.Sort((a, b) => b.Size.CompareTo(a.Size));
            Stats.Trim();
            Window.Dispatcher.BeginInvoke(new Action(() => ScanDone(all, drives.Count + " drives")));
        });
    }

    // --- automatic history ---

    void ComparePrevious()
    {
        if (root == null || busy) return;
        string previous = History.List(root.FullPath).FirstOrDefault(f => f != ownHistoryFile);
        if (previous == null) { Error("No earlier scan of " + root.FullPath + " is stored yet. This one was just saved, so a later scan can be compared to it."); return; }
        CompareWith(previous);
    }

    void ShowHistoryMenu(Button anchor)
    {
        if (root == null) return;
        var m = new ContextMenu { Template = menu.Template, Foreground = menu.Foreground, FontSize = menu.FontSize, HasDropShadow = false,
            PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, VerticalOffset = 6 };
        foreach (var file in History.List(root.FullPath))
        {
            string f = file;
            var item = new MenuItem { Style = (Style)Window.Resources["MI"],
                Header = History.TakenAt(f).ToString("yyyy-MM-dd HH:mm") + (f == ownHistoryFile ? "  (this scan)" : ""),
                InputGestureText = Brand.Fmt(new FileInfo(f).Length) };
            item.Click += delegate { CompareWith(f); };
            m.Items.Add(item);
        }
        if (m.Items.Count == 0)
            m.Items.Add(new MenuItem { Style = (Style)Window.Resources["MI"], Header = "No stored scans yet", IsEnabled = false });
        m.IsOpen = true;
    }

    void RunSearch()
    {
        if (root == null || busy) return;
        string name = findName.Text.Trim();
        var q = new Query { Name = name.Length == 0 ? null : name, MinSize = ParseSize(findSize.Text) };
        int days;
        if (int.TryParse(findAge.Text.Trim(), out days) && days > 0) q.NotTouchedSince = DateTime.UtcNow.AddDays(-days);
        findSort.Clear();
        findHint.Visibility = Visibility.Collapsed;
        StartJob(1, () =>
        {
            var hits = Finder.Run(root, q);
            Window.Dispatcher.BeginInvoke(new Action(() =>
            {
                ResolveDirs(hits);
                findSort.Set(hits.Select(f => new FoundRow { File = f, SizeValue = f.Size, DateValue = f.Modified,
                    Name = f.Name, SizeText = Brand.Fmt(f.Size), ModifiedText = ModifiedText(f), Folder = f.Dir.FullPath }));
                findHint.Text = "No files matched.";
                findHint.Visibility = findSort.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                EndJob(string.Format("Found {0:N0} files{1}  •  {2}  •  {3:0.0}s", findSort.Items.Count,
                    findSort.Items.Count >= q.Max ? " (limit reached)" : "", Brand.Fmt(hits.Sum(f => f.Size)), sw.Elapsed.TotalSeconds));
            }));
        });
    }

    void RunDupes()
    {
        if (root == null || busy) return;
        var q = new Query { MinSize = Math.Max(1, ParseSize(dupSize.Text)), Max = 500000 };
        dups.Clear();
        dupSummary.Text = "";
        dupHint.Visibility = Visibility.Collapsed;
        StartJob(2, () =>
        {
            var files = Finder.Run(root, q);
            var groups = Dupes.Find(files, (done, total) => { Scanner.FilesSeen = done; Scanner.Total = total; });
            Window.Dispatcher.BeginInvoke(new Action(() =>
            {
                long wasted = 0;
                foreach (var g in groups)
                {
                    ResolveDirs(g);
                    wasted += (g.Count - 1) * g[0].Size;
                    dups.Add(new DupRow { Name = g[0].Name, SizeText = Brand.Fmt(g[0].Size),
                        Info = string.Format("{0} copies · {1} recoverable", g.Count, Brand.Fmt((g.Count - 1) * g[0].Size)) });
                    foreach (var f in g) dups.Add(new DupRow { File = f, Name = f.Name, SizeText = Brand.Fmt(f.Size), Info = f.Dir.FullPath });
                }
                dupSummary.Text = string.Format("{0:N0} groups · {1} recoverable · copies inside program folders are often required there", groups.Count, Brand.Fmt(wasted));
                dupHint.Text = "No duplicates found.";
                dupHint.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                EndJob(string.Format("{0:N0} duplicate groups in {1:N0} files  •  {2} recoverable  •  {3:0.0}s{4}",
                    groups.Count, files.Count, Brand.Fmt(wasted), sw.Elapsed.TotalSeconds, Scanner.Cancel ? "  •  STOPPED" : ""));
            }));
        });
    }

    void SetBusy(bool b)
    {
        busy = b;
        scanBtn.Content = b ? "Stop" : "Scan";
        exportBtn.IsEnabled = !b;
        if (b) { sw.Restart(); timer.Start(); }
        else { timer.Stop(); sw.Stop(); progress.Width = 0; }
    }

    void ShowProgress()
    {
        long seen = Scanner.FilesSeen, total = Scanner.Total;
        if (phase == 3)
            status.Text = string.Format("Working…  •  {0:0}s", sw.Elapsed.TotalSeconds);
        else if (phase == 2)
            status.Text = total > 0
                ? string.Format("Comparing… {0} of {1}  •  {2:0}s", Brand.Fmt(seen), Brand.Fmt(total), sw.Elapsed.TotalSeconds)
                : string.Format("Collecting files…  •  {0:0}s", sw.Elapsed.TotalSeconds);
        else if (total > 0)
            status.Text = string.Format("{0} MFT… {1:0}%  •  {2:N0} / {3:N0} records  •  {4:0}s", phase == 1 ? "Searching" : "Reading",
                100.0 * seen / total, seen, total, sw.Elapsed.TotalSeconds);
        else
            status.Text = string.Format("{0}… {1:N0} {2}  •  {3:0}s  •  {4}", phase == 1 ? "Searching" : "Scanning", seen,
                phase == 1 ? "folders" : "files", sw.Elapsed.TotalSeconds, Scanner.Current);
        progress.Width = total > 0 ? ((FrameworkElement)progress.Parent).ActualWidth * Math.Min(1.0, (double)seen / total) : 0;
    }

    // ---------- scanning ----------

    void StartOrStop()
    {
        if (busy) { Scanner.Cancel = true; return; }

        string path = pathBox.Text.Trim();
        if (path.Length == 2 && path[1] == ':') path += "\\";
        if (!Directory.Exists(path)) { Error("Folder not found: " + path); return; }

        Stats.Reset();
        Scanner.Cancel = false;
        Scanner.FilesSeen = 0;
        Scanner.Total = 0;
        Scanner.DeniedCount = 0;
        root = null;
        rows.Clear();
        findSort.Clear();
        dups.Clear();
        cmpSort.Clear();
        dirCache.Clear();
        dupSummary.Text = "";
        emptyState.Visibility = Visibility.Collapsed;
        SetBusy(true);

        // Big stack: recursion depth follows folder depth, which can be thousands with long paths.
        new Thread(() =>
        {
            Node r = null;
            string mode;
            try
            {
                if (Mft.IsNtfsRoot(path)) r = Mft.Scan(path);
                mode = r != null ? "MFT (fast)" : "classic (not an NTFS volume root)";
            }
            catch (Exception ex) { mode = "classic — MFT unavailable: " + ex.Message; }
            if (r == null)
            {
                Stats.Reset();
                Scanner.FilesSeen = 0;
                Scanner.Total = 0;
                r = new Node { Name = path };
                Scanner.Scan(r, path);
                Stats.Trim();
            }
            Window.Dispatcher.BeginInvoke(new Action(() => ScanDone(r, mode)));
        }, 256 * 1024 * 1024) { IsBackground = true }.Start();
    }

    void ScanDone(Node r, string mode)
    {
        SetBusy(false);
        root = r;
        var top = new Row { Node = r };
        rows.Add(top);
        Expand(top);
        Select(top);
        RefreshLists();
        RefreshDrives();
        ownHistoryFile = null;
        var toStore = r;
        new Thread(() => { History.Save(toStore); var newest = History.List(toStore.FullPath).FirstOrDefault();
            Window.Dispatcher.BeginInvoke(new Action(() => { ownHistoryFile = newest; })); }, 64 * 1024 * 1024) { IsBackground = true }.Start();
        status.Text = string.Format("{0}{1}  •  {2:N0} files  •  {3:0.0}s  •  {4} scan{5}{6}",
            Scanner.Cancel ? "STOPPED (partial)  •  " : "", Brand.Fmt(r.Size), r.Files, sw.Elapsed.TotalSeconds, mode,
            Scanner.DeniedCount > 0 ? string.Format("  •  {0:N0} folders not accessible", Scanner.DeniedCount) : "", VolumeInfo(r.FullPath));
        Find<Button>("elevateBtn").Visibility = !IsAdmin && (Scanner.DeniedCount > 0 || mode.StartsWith("classic")) ? Visibility.Visible : Visibility.Collapsed;
    }

    static string VolumeInfo(string path)
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(path));
            return string.Format("  •  volume: {0} used of {1}, {2} free", Brand.Fmt(d.TotalSize - d.TotalFreeSpace), Brand.Fmt(d.TotalSize), Brand.Fmt(d.TotalFreeSpace));
        }
        catch (Exception) { return ""; } // UNC paths etc. have no DriveInfo
    }

    void Rescan(List<Node> nodes)
    {
        if (busy || nodes.Count == 0) return;
        foreach (var n in nodes) { var x = n; Stats.Top.RemoveAll(t => t.Dir == x || IsUnder(t.Dir, x)); }
        var old = nodes.Select(n => new[] { n.Size, n.Files }).ToList();
        Scanner.Cancel = false;
        Scanner.FilesSeen = 0;
        Scanner.Total = 0;
        Scanner.DeniedCount = 0;
        SetBusy(true);
        new Thread(() =>
        {
            Scanner.CollectExt = false;
            foreach (var n in nodes)
            {
                n.Dirs = new List<Node>();
                n.Size = n.Files = 0;
                n.Denied = false;
                Scanner.Scan(n, n.FullPath);
            }
            Scanner.CollectExt = true;
            Window.Dispatcher.BeginInvoke(new Action(() =>
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    var n = nodes[i];
                    long dSize = n.Size - old[i][0], dFiles = n.Files - old[i][1];
                    for (var a = n.Parent; a != null; a = a.Parent) { a.Size += dSize; a.Files += dFiles; }
                    if (n.Parent != null) n.Parent.Dirs.Sort((a, b) => b.Size.CompareTo(a.Size));
                    var row = RowOf(n);
                    if (row != null && row.IsExpanded) { Collapse(row); Expand(row); }
                }
                SetBusy(false);
                Stats.Trim();
                RefreshViews();
                status.Text = string.Format("Rescanned in {0:0.0}s: {1}{2}", sw.Elapsed.TotalSeconds,
                    string.Join(", ", nodes.Select(n => n.FullPath + " (" + Brand.Fmt(n.Size) + ")")), VolumeInfo(nodes[0].FullPath));
            }));
        }, 256 * 1024 * 1024) { IsBackground = true }.Start();
    }

    // ---------- tree ----------

    static bool IsUnder(Node a, Node b)
    {
        for (var x = a == null ? null : a.Parent; x != null; x = x.Parent) if (x == b) return true;
        return false;
    }

    static Row RowAt(DependencyObject d, string partName)
    {
        for (; d != null && !(d is ListBoxItem); d = VisualTreeHelper.GetParent(d))
            if (partName != null && d is FrameworkElement && ((FrameworkElement)d).Name == partName)
                return ((FrameworkElement)d).DataContext as Row;
        return partName == null && d != null ? ((ListBoxItem)d).DataContext as Row : null;
    }

    Row RowOf(Node n) { return rows.FirstOrDefault(r => r.Node == n); }

    void Toggle(Row r) { if (r.IsExpanded) Collapse(r); else Expand(r); }

    // Children are built on expand; files aren't kept in memory, they're re-listed per folder on demand.
    void Expand(Row r)
    {
        if (busy || !r.CanExpand || r.IsExpanded) return;
        var n = r.Node;
        var items = n.Dirs.Select(c => new Row { Node = c, Level = r.Level + 1 }).ToList();

        var files = new List<FileEntry>();
        Scanner.Enum(n.FullPath, (name, attr, size, modified) => { if ((attr & Scanner.DIR) == 0) files.Add(new FileEntry { Dir = n, Name = name, Size = size, Modified = modified }); });
        files.Sort((a, b) => b.Size.CompareTo(a.Size));
        items.AddRange(files.Take(MaxFilesShown).Select(f => new Row { File = f, Level = r.Level + 1 }));
        items.Sort((a, b) => (b.Node != null ? b.Node.Size : b.File.Size).CompareTo(a.Node != null ? a.Node.Size : a.File.Size));
        if (files.Count > MaxFilesShown)
            items.Add(new Row { Level = r.Level + 1, File = new FileEntry { Dir = n, Summary = true, Size = files.Skip(MaxFilesShown).Sum(f => f.Size),
                Name = string.Format("… {0:N0} more smaller files", files.Count - MaxFilesShown) } });

        int at = rows.IndexOf(r) + 1;
        foreach (var it in items) rows.Insert(at++, it);
        r.IsExpanded = true;
    }

    void Collapse(Row r)
    {
        int i = rows.IndexOf(r);
        while (i + 1 < rows.Count && rows[i + 1].Level > r.Level) rows.RemoveAt(i + 1);
        r.IsExpanded = false;
    }

    void Select(Row r)
    {
        treeList.SelectedItem = r;
        treeList.ScrollIntoView(r);
        treeList.UpdateLayout();
        var item = treeList.ItemContainerGenerator.ContainerFromItem(r) as ListBoxItem;
        if (item != null) item.Focus();
    }

    // Expands the path down to n and returns its row.
    Row Reveal(Node n)
    {
        if (n == null) return null;
        if (n.Parent == null) return RowOf(n);
        var p = Reveal(n.Parent);
        if (p == null) return null;
        Expand(p);
        return RowOf(n);
    }

    void RevealFile(FileEntry f)
    {
        var dir = Reveal(f.Dir);
        if (dir == null) return;
        tabTree.IsChecked = true;
        var row = rows.FirstOrDefault(r => r.File != null && r.File.Dir == f.Dir && !r.File.Summary && r.File.Name == f.Name);
        Select(row ?? dir);
    }

    void TreeKey(KeyEventArgs e)
    {
        var r = treeList.SelectedItem as Row;
        if (r == null) return;
        if (e.Key == Key.Right && r.CanExpand && !r.IsExpanded) Expand(r);
        else if (e.Key == Key.Left && r.IsExpanded) Collapse(r);
        else if (e.Key == Key.Left && r.Level > 0)
        {
            int i = rows.IndexOf(r);
            while (i > 0 && rows[i].Level >= r.Level) i--;
            Select(rows[i]);
        }
        else if (e.Key == Key.Enter && r.Node != null) Toggle(r);
        else { HandleKey(e, MenuTags()); return; }
        e.Handled = true;
    }

    void RefreshLists()
    {
        long total = root == null ? 0 : root.Size;
        bool cats = byCat.IsChecked == true;
        extSort.Label("name", cats ? "CATEGORY" : "EXTENSION");
        var groups = cats
            ? Categories.Group(Stats.Ext)
            : Stats.Ext.Where(kv => kv.Value[1] > 0).OrderByDescending(kv => kv.Value[0]).ToList();
        extSort.Set(groups.Select(kv => new ExtRow
        {
            Ext = kv.Key, SizeValue = kv.Value[0], CountValue = kv.Value[1], AvgValue = kv.Value[0] / Math.Max(1, kv.Value[1]),
            SizeText = Brand.Fmt(kv.Value[0]), PctText = Brand.Pct(kv.Value[0], total),
            CountText = kv.Value[1].ToString("N0"), AvgText = Brand.Fmt(kv.Value[0] / Math.Max(1, kv.Value[1]))
        }));
        topSort.Set(Stats.Top.Select(f => new TopRow
        {
            File = f, SizeValue = f.Size, Name = f.Name, SizeText = Brand.Fmt(f.Size), PctText = Brand.Pct(f.Size, total), Folder = f.Dir.FullPath
        }));
    }

    void RefreshViews()
    {
        foreach (var r in rows) r.Changed();
        RefreshLists();
    }

    // ---------- file operations ----------

    List<object> MenuTags()
    {
        if (viewTop.Visibility == Visibility.Visible) return topList.SelectedItems.Cast<TopRow>().Select(t => (object)t.File).ToList();
        if (viewSearch.Visibility == Visibility.Visible) return foundList.SelectedItems.Cast<FoundRow>().Select(t => (object)t.File).ToList();
        if (viewDrives.Visibility == Visibility.Visible) return new List<object>();
        if (viewDup.Visibility == Visibility.Visible) return dupList.SelectedItems.Cast<DupRow>().Where(d => d.File != null).Select(d => (object)d.File).ToList();
        return treeList.SelectedItems.Cast<Row>().Select(r => r.Tag).Where(t => t != null).ToList();
    }

    void MenuAction(string action, List<object> tags)
    {
        object first = tags.FirstOrDefault();
        switch (action)
        {
            case "open": Open(first); break;
            case "explorer": ShowInExplorer(first); break;
            case "filelabs": OpenInFileLabs(first); break;
            case "copypath": CopyPath(tags); break;
            case "copy": CopyMove(tags, false); break;
            case "move": CopyMove(tags, true); break;
            case "recycle": Delete(tags, false); break;
            case "delete": Delete(tags, true); break;
            case "rescan": RescanTag(first); break;
            case "props": ShowProperties(first); break;
        }
    }

    void HandleKey(KeyEventArgs e, List<object> tags)
    {
        if (tags.Count == 0) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0, ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Delete) Delete(tags, shift);
        else if (e.Key == Key.F5) RescanTag(tags[0]);
        else if (e.Key == Key.Enter) Open(tags[0]);
        else if (ctrl && e.Key == Key.C) CopyPath(tags);
        else return;
        e.Handled = true;
    }

    static string PathOf(object tag)
    {
        var n = tag as Node;
        if (n != null) return n.FullPath;
        var f = tag as FileEntry;
        return f == null || f.Summary ? null : f.FullPath;
    }

    void Open(object tag)
    {
        string p = PathOf(tag);
        if (p == null) return;
        try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); }
        catch (Exception ex) { Error(ex.Message); }
    }

    void ShowInExplorer(object tag)
    {
        string p = PathOf(tag);
        if (p != null) Process.Start("explorer.exe", "/select,\"" + p + "\"");
    }

    void CopyPath(List<object> tags)
    {
        var paths = tags.Select(PathOf).Where(p => p != null).ToList();
        if (paths.Count > 0) Clipboard.SetText(string.Join("\r\n", paths));
    }

    void ShowProperties(object tag)
    {
        string p = PathOf(tag);
        if (p == null) return;
        var info = new SHELLEXECUTEINFO { lpVerb = "properties", lpFile = p, nShow = 1, fMask = 0xC /*SEE_MASK_INVOKEIDLIST*/, hwnd = new WindowInteropHelper(Window).Handle };
        info.cbSize = Marshal.SizeOf(info);
        ShellExecuteEx(ref info);
    }

    void RescanTag(object tag)
    {
        var n = tag as Node ?? (tag is FileEntry ? ((FileEntry)tag).Dir : null);
        if (n != null) Rescan(new List<Node> { n });
    }

    // ponytail: shell operations go through SHFileOperation, which fails on paths > 260 chars (an error dialog is shown).
    void Delete(List<object> tags, bool permanent)
    {
        if (busy) return;
        var items = tags.Where(t => PathOf(t) != null).ToList();
        if (items.Count == 0) return;
        if (items.Any(t => t is Node && ((Node)t).Parent == null)) { Error("The scan root can't be deleted."); return; }
        long size = items.Sum(t => t is Node ? ((Node)t).Size : ((FileEntry)t).Size);
        // One item to the Recycle Bin: the shell asks. Otherwise we ask once for the whole selection.
        bool ask = permanent || items.Count > 1;
        if (ask && MessageBox.Show(Window, string.Format("{0} {1:N0} item(s), {2}?\n\n{3}{4}",
                permanent ? "PERMANENTLY delete" : "Move to the Recycle Bin:", items.Count, Brand.Fmt(size),
                string.Join("\n", items.Take(10).Select(PathOf)) + (items.Count > 10 ? "\n…" : ""),
                permanent ? "\n\nThis can't be undone." : ""),
                permanent ? "Delete permanently" : "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var uiOption = ask ? UIOption.OnlyErrorDialogs : UIOption.AllDialogs;
        var recycle = permanent ? RecycleOption.DeletePermanently : RecycleOption.SendToRecycleBin;
        foreach (var t in items)
        {
            try
            {
                if (t is Node) FileSystem.DeleteDirectory(PathOf(t), uiOption, recycle, UICancelOption.DoNothing);
                else FileSystem.DeleteFile(PathOf(t), uiOption, recycle, UICancelOption.DoNothing);
            }
            catch (Exception ex) { Error(ex.Message); break; }
        }
        AfterChange(items, null);
    }

    void CopyMove(List<object> tags, bool move)
    {
        if (busy) return;
        var items = tags.Where(t => PathOf(t) != null).ToList();
        if (items.Count == 0) return;
        if (items.Any(t => t is Node && ((Node)t).Parent == null)) { Error("The scan root can't be copied or moved from here."); return; }
        string dest;
        using (var dlg = new WinForms.FolderBrowserDialog { Description = (move ? "Move " : "Copy ") + items.Count + " item(s) to:" })
        {
            if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;
            dest = dlg.SelectedPath;
        }
        foreach (var t in items)
        {
            string p = PathOf(t);
            var n = t as Node;
            if (n != null && (dest.TrimEnd('\\') + "\\").StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                Error("The destination is inside " + p + ".");
                continue;
            }
            string target = Scanner.Join(dest, n != null ? n.Name : ((FileEntry)t).Name);
            try
            {
                if (n != null && move) FileSystem.MoveDirectory(p, target, UIOption.AllDialogs, UICancelOption.DoNothing);
                else if (n != null) FileSystem.CopyDirectory(p, target, UIOption.AllDialogs, UICancelOption.DoNothing);
                else if (move) FileSystem.MoveFile(p, target, UIOption.AllDialogs, UICancelOption.DoNothing);
                else FileSystem.CopyFile(p, target, UIOption.AllDialogs, UICancelOption.DoNothing);
            }
            catch (Exception ex) { Error(ex.Message); break; }
        }
        AfterChange(move ? items : new List<object>(), dest);
    }

    // After an operation: drop what disappeared, rescan what may have changed (source partially gone, destination inside the scan).
    void AfterChange(IEnumerable<object> changed, string touchedDir)
    {
        var rescan = new List<Node>();
        foreach (var t in changed)
        {
            if (PathOf(t) == null) continue; // e.g. the "N more smaller files" row
            if (!Scanner.Exists(PathOf(t))) Removed(t);
            else if (t is Node) rescan.Add((Node)t);
        }
        var d = touchedDir == null ? null : FindNodeByPath(touchedDir);
        if (d != null) rescan.Add(d);
        rescan = rescan.Where(a => !rescan.Any(b => b != a && IsUnder(a, b))).ToList();
        if (rescan.Count > 0) Rescan(rescan);
        else RefreshViews();
    }

    Node FindNodeByPath(string path) { return FindNodeByPath(path, false); }

    Node FindNodeByPath(string path, bool exact)
    {
        if (root == null) return null;
        string rp = root.FullPath.TrimEnd('\\');
        path = path.TrimEnd('\\');
        if (!path.Equals(rp, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(rp + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        var n = root;
        foreach (var part in path.Substring(rp.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var c = n.Dirs.FirstOrDefault(x => x.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (c == null) return exact ? null : n;
            n = c;
        }
        return n;
    }

    void Removed(object tag)
    {
        var n = tag as Node;
        var f = tag as FileEntry;
        Node dir = n != null ? n.Parent : f.Dir;
        long size = n != null ? n.Size : f.Size, files = n != null ? n.Files : 1;

        if (n != null)
        {
            var row = RowOf(n);
            if (row != null) { Collapse(row); rows.Remove(row); }
            Stats.Top.RemoveAll(t => t.Dir == n || IsUnder(t.Dir, n));
            dir.Dirs.Remove(n);
        }
        else
        {
            var row = rows.FirstOrDefault(r => r.File != null && r.File.Dir == f.Dir && !r.File.Summary && r.File.Name == f.Name);
            if (row != null) rows.Remove(row);
            Stats.Top.RemoveAll(t => t.Dir == f.Dir && t.Name == f.Name);
            Stats.AddExt(Stats.ExtOf(f.Name), -f.Size, -1);
        }
        for (var a = dir; a != null; a = a.Parent) { a.Size -= size; a.Files -= files; }
        DropRows(n, f);
        status.Text = "Freed " + Brand.Fmt(size) + "  •  " + PathOf(tag) + VolumeInfo(PathOf(tag));
    }

    // Drops deleted files from the search and duplicate lists; a group with one copy left is no longer a duplicate.
    void DropRows(Node dir, FileEntry file)
    {
        Func<FileEntry, bool> gone = f => file != null ? (f.Dir == file.Dir && f.Name == file.Name) : (f.Dir == dir || IsUnder(f.Dir, dir));
        findSort.Remove(r => gone(r.File));
        foreach (var r in dups.Where(r => r.File != null && gone(r.File)).ToList()) dups.Remove(r);
        for (int i = dups.Count - 1; i >= 0; i--)
        {
            if (dups[i].File != null) continue;
            int copies = 0;
            while (i + copies + 1 < dups.Count && dups[i + copies + 1].File != null) copies++;
            if (copies < 2) for (int k = i + copies; k >= i; k--) dups.RemoveAt(k);
        }
    }

    void ShowExportMenu(Button anchor)
    {
        if (root == null || busy) return;
        var m = new ContextMenu { Template = menu.Template, Foreground = menu.Foreground, FontSize = menu.FontSize, HasDropShadow = false,
            PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, VerticalOffset = 6 };
        var items = new[] { "All folders (CSV)", "Report (HTML)", "This view (CSV)" };
        for (int i = 0; i < items.Length; i++)
        {
            int which = i;
            var item = new MenuItem { Style = (Style)Window.Resources["MI"], Header = items[i] };
            item.Click += delegate { if (which == 0) Export(); else if (which == 1) ExportHtml(); else ExportView(); };
            m.Items.Add(item);
        }
        m.IsOpen = true;
    }

    string Ask(string filter, string name)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = name };
        return dlg.ShowDialog(Window) == true ? dlg.FileName : null;
    }

    static string Csv(string v) { return "\"" + (v ?? "").Replace("\"", "\"\"") + "\""; }

    // Exports whatever list is on screen, so a search or a duplicate hunt can leave the app.
    void ExportView()
    {
        var rowsOut = new List<string>();
        string name = "spacescan-view.csv";
        if (viewExt.Visibility == Visibility.Visible)
        {
            rowsOut.Add("Extension;Size;Size (bytes);Files");
            foreach (var kv in Stats.Ext.Where(kv => kv.Value[1] > 0).OrderByDescending(kv => kv.Value[0]))
                rowsOut.Add(string.Join(";", Csv(kv.Key), Csv(Brand.Fmt(kv.Value[0])), kv.Value[0].ToString(), kv.Value[1].ToString()));
            name = "spacescan-filetypes.csv";
        }
        else if (viewTop.Visibility == Visibility.Visible || viewSearch.Visibility == Visibility.Visible)
        {
            bool search = viewSearch.Visibility == Visibility.Visible;
            var files = search ? findSort.Items.Select(f => f.File) : Stats.Top.AsEnumerable();
            rowsOut.Add("Name;Size;Size (bytes);Modified;Folder");
            foreach (var f in files)
                rowsOut.Add(string.Join(";", Csv(f.Name), Csv(Brand.Fmt(f.Size)), f.Size.ToString(), Csv(ModifiedText(f)), Csv(f.Dir.FullPath)));
            name = search ? "spacescan-search.csv" : "spacescan-largest.csv";
        }
        else if (viewDup.Visibility == Visibility.Visible)
        {
            rowsOut.Add("Group;Name;Size;Size (bytes);Folder");
            int group = 0;
            foreach (var d in dups)
            {
                if (d.File == null) { group++; continue; }
                rowsOut.Add(string.Join(";", group.ToString(), Csv(d.File.Name), Csv(Brand.Fmt(d.File.Size)), d.File.Size.ToString(), Csv(d.File.Dir.FullPath)));
            }
            name = "spacescan-duplicates.csv";
        }
        else if (viewCmp.Visibility == Visibility.Visible)
        {
            rowsOut.Add("Folder;Before;Now;Change");
            foreach (var c in cmpSort.Items) rowsOut.Add(string.Join(";", Csv(c.Path), Csv(c.BeforeText), Csv(c.AfterText), Csv(c.DeltaText)));
            name = "spacescan-changes.csv";
        }
        else { Export(); return; } // folder view: the full tree export
        if (rowsOut.Count < 2) { Error("This view is empty."); return; }
        string file = Ask("CSV (*.csv)|*.csv", name);
        if (file == null) return;
        try
        {
            File.WriteAllLines(file, rowsOut, new UTF8Encoding(true));
            status.Text = "Exported: " + file;
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    void ExportHtml()
    {
        string file = Ask("HTML report (*.html)|*.html", "spacescan-report.html");
        if (file != null) ExportHtmlTo(file, true);
    }

    void ExportHtmlTo(string file, bool open)
    {
        try
        {
            File.WriteAllText(file, Report.Html(root, Stats.Ext, Stats.Top, lastDiff), new UTF8Encoding(true));
            status.Text = "Report written: " + file;
            if (open) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    void Export()
    {
        if (root == null || busy) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "spacescan.csv" };
        if (dlg.ShowDialog(Window) != true) return;
        try
        {
            File.WriteAllLines(dlg.FileName, Report.FolderCsv(root), new UTF8Encoding(true));
            status.Text = "Exported: " + dlg.FileName;
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    // ---------- entry point ----------

    static void ShowCrash(object ex)
    {
        string log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SpaceScan-crash.log");
        try { File.AppendAllText(log, DateTime.Now + "\r\n" + ex + "\r\n\r\n"); } catch { }
        MessageBox.Show("SpaceScan hit an error (saved to " + log + "):\r\n\r\n" + ex, "SpaceScan", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    [STAThread]
    static int Main(string[] args)
    {
        // A bare path (Explorer's or File Labs' "Scan with SpaceScan") opens the window on it;
        // anything with options runs headless and writes files.
        if (args.Any(a => a.StartsWith("-"))) return Cli.Run(args);
        if (args.Length > 0) startPath = args[0];
        AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowCrash(e.ExceptionObject);
        var app = new Application();
        app.DispatcherUnhandledException += (s, e) => { ShowCrash(e.Exception); e.Handled = true; };
        app.Run(new MainUI().Window);
        return 0;
    }
}
