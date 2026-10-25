using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// What's new, as the studio's other apps show it: every release in a list on the left, under the month it came out,
// and the chosen one on the right under its 960 x 170 banner. The notes are whatsnew.txt, built into the exe (build.cmd),
// so it works offline and shows every release. A release with a picture of its own (WhatsNew\x.y.jpg, built in) shows
// it; the others show the picture of the month they came out (WhatsNew\mMM.jpg). C# 5 (csc from the .NET Framework), so no newer syntax here.
class WhatsNew
{
    internal class Release { public string Version, Title; public DateTime Date; public List<string> Lines = new List<string>(); }

    static Window open;

    // Once after an update; a first install has nothing new to tell. Marked seen when the window closes, not when it
    // opens, so one that never made it to the screen comes back next time. Kept in the data folder (whatsnew-seen.txt);
    // a data folder that doesn't exist yet is a first install.
    internal static void OpenOnceAfterUpdate(Window owner)
    {
        string current = Updater.Current.ToString(), file = Path.Combine(Ecosystem.Data, "whatsnew-seen.txt"), seen = null;
        try { seen = File.Exists(file) ? File.ReadAllText(file).Trim() : null; } catch (Exception) { }
        if (seen == current) return;
        Action mark = delegate
        {
            try { Directory.CreateDirectory(Ecosystem.Data); File.WriteAllText(file, current); } catch (Exception) { } // shown again next time, no harm
        };
        if (seen == null && !Directory.Exists(Ecosystem.Data)) { mark(); return; }
        Open(owner);
        open.Closed += delegate { mark(); };
    }

    internal static void Open(Window owner)
    {
        if (open != null) { open.Activate(); return; }
        open = new WhatsNew(owner).window;
        open.Closed += delegate { open = null; };
        open.Show();
    }

    internal static List<Release> Load()
    {
        using (var s = typeof(WhatsNew).Assembly.GetManifestResourceStream("whatsnew.txt"))
        using (var r = new StreamReader(s)) return Parse(r.ReadToEnd());
    }

    // "## 1.4.0 | 2026-10-23 | Title", then the release's lines; "# " lines are comments.
    internal static List<Release> Parse(string text)
    {
        var list = new List<Release>();
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string[] head = line.StartsWith("## ") ? line.Substring(3).Split(new[] { " | " }, 3, StringSplitOptions.None) : null;
            if (head != null && head.Length == 3)
                list.Add(new Release { Version = head[0].Trim(), Date = DateTime.ParseExact(head[1].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture), Title = head[2].Trim() });
            else if (line.StartsWith("# ") || line.Trim() == "" || list.Count == 0) continue;
            else list[list.Count - 1].Lines.Add(line);
        }
        return list;
    }

    readonly Window window;
    readonly Window owner;
    readonly List<Release> releases = Load();
    readonly StackPanel nav = new StackPanel();
    readonly Border banner = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Left };
    readonly StackPanel notes = new StackPanel();
    readonly ScrollViewer notesScroll;
    int shown = -1;

    WhatsNew(Window owner)
    {
        this.owner = owner;
        window = new Window
        {
            Title = "What’s new — SpaceScan", Width = 1240, Height = 720, MinWidth = 700, MinHeight = 420, // room for the list and a 960 x 170 banner
            Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = Brand.Ground, Foreground = Brand.Text, FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5, UseLayoutRounding = true,
        };
        window.SourceInitialized += delegate { MainUI.Dwm(new System.Windows.Interop.WindowInteropHelper(window).Handle, 20, 1); }; // dark title bar
        window.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape) window.Close();
            else if (e.Key == Key.Down || e.Key == Key.Up) { Show(Math.Max(0, Math.Min(releases.Count - 1, shown + (e.Key == Key.Down ? 1 : -1)))); e.Handled = true; }
        };

        var close = new Button { Content = "Close", Style = (Style)owner.FindResource("Primary"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        close.Click += delegate { window.Close(); };

        var navScroll = new ScrollViewer { Content = nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 200, Margin = new Thickness(0, 0, 16, 0) };
        notesScroll = new ScrollViewer { Content = notes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
        var right = new DockPanel();
        DockPanel.SetDock(banner, Dock.Top);
        right.Children.Add(banner);
        right.Children.Add(notesScroll);

        var body = new DockPanel { Margin = new Thickness(18, 16, 18, 14) };
        DockPanel.SetDock(close, Dock.Bottom);
        DockPanel.SetDock(navScroll, Dock.Left);
        body.Children.Add(close);
        body.Children.Add(navScroll);
        body.Children.Add(right);
        window.Content = body;

        BuildNav();
        // the version running, which is the newest unless this build is older than its own notes
        Show(Math.Max(0, releases.FindIndex(r => r.Version == Updater.Current.ToString())));
    }

    void BuildNav()
    {
        string month = null;
        for (int i = 0; i < releases.Count; i++)
        {
            var r = releases[i];
            string m = r.Date.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
            if (m != month)
            {
                month = m;
                nav.Children.Add(new TextBlock { Text = m, FontSize = 11.5, Foreground = Brand.Dim, Margin = new Thickness(8, nav.Children.Count == 0 ? 0 : 12, 0, 4) });
            }
            int index = i;
            var row = new DockPanel { Width = 160 }; // the button's template centres its content: the row spans it
            var when = new TextBlock { Text = i == 0 ? "Latest" : r.Date.ToString("d MMM", CultureInfo.InvariantCulture), FontSize = 11.5, Foreground = Brand.Dim, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(when, Dock.Right);
            row.Children.Add(when);
            row.Children.Add(new TextBlock { Text = r.Version, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = row, Style = (Style)owner.FindResource("Ghost"), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Padding = new Thickness(0), Margin = new Thickness(0, 1, 0, 1), ToolTip = r.Title };
            System.Windows.Automation.AutomationProperties.SetName(button, r.Version + ", " + r.Title);
            button.Click += delegate { Show(index); };
            nav.Children.Add(button);
        }
    }

    static readonly Brush Selected = new SolidColorBrush(Color.FromArgb(0x2B, 0x4C, 0x8D, 0xFF)), Body = new SolidColorBrush(Color.FromRgb(0xD6, 0xDE, 0xEA));

    void Show(int i)
    {
        if (i == shown || i < 0 || i >= releases.Count) return;
        shown = i;
        var r = releases[i];
        var buttons = nav.Children.OfType<Button>().ToList();
        for (int b = 0; b < buttons.Count; b++) buttons[b].Background = b == i ? Selected : Brushes.Transparent;
        buttons[i].BringIntoView();

        banner.Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = Banner(r) };
        notes.Children.Clear();
        notes.Children.Add(new TextBlock { Text = r.Date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture), Foreground = Brand.Dim, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var line in r.Lines)
        {
            string text = line.Trim();
            if (text.StartsWith("- "))
            {
                var point = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var dot = new TextBlock { Text = "•", Foreground = Brand.Accent, Margin = new Thickness(2, 0, 10, 0) };
                DockPanel.SetDock(dot, Dock.Left);
                point.Children.Add(dot);
                point.Children.Add(Rich(text.Substring(2)));
                notes.Children.Add(point);
            }
            else if (text.StartsWith("**") && text.EndsWith("**") && text.Length > 4 && !text.Substring(2, text.Length - 4).Contains("**"))
                notes.Children.Add(new TextBlock { Text = text.Substring(2, text.Length - 4), FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 12, 0, 4) });
            else
            {
                var p = Rich(text);
                p.Margin = new Thickness(0, 4, 0, 6);
                notes.Children.Add(p);
            }
        }
        notesScroll.ScrollToTop();
    }

    // A line of the notes with its **bold** parts in bold.
    static TextBlock Rich(string text)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20, Foreground = Body };
        var parts = text.Split(new[] { "**" }, StringSplitOptions.None);
        for (int i = 0; i < parts.Length; i++)
            if (parts[i] != "") t.Inlines.Add(new Run(parts[i]) { FontWeight = i % 2 == 1 ? FontWeights.SemiBold : FontWeights.Normal });
        return t;
    }

    // The release's picture (WhatsNew.x.y.jpg, built in), or the one of the month it came out (WhatsNew.mMM.jpg).
    internal static Stream Picture(Release r)
    {
        string minor = string.Join(".", r.Version.Split('.').Take(2));
        return typeof(WhatsNew).Assembly.GetManifestResourceStream("WhatsNew." + minor + ".jpg")
            ?? typeof(WhatsNew).Assembly.GetManifestResourceStream("WhatsNew.m" + r.Date.ToString("MM") + ".jpg");
    }

    static UIElement Banner(Release r)
    {
        var picture = new BitmapImage();
        using (var stream = Picture(r))
        {
            picture.BeginInit();
            picture.CacheOption = BitmapCacheOption.OnLoad;
            picture.StreamSource = stream;
            picture.EndInit();
        }
        picture.Freeze();
        return new Image { Source = picture, Width = 960, Height = 170, Stretch = Stretch.UniformToFill };
    }
}
