using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Assist;

/// Lists the supported console agents, shows which are installed, and lets the user pick one.
public partial class PickerWindow : Window
{
    static readonly Brush Fg = Frozen(Color.FromRgb(0xEC, 0xEC, 0xEE));
    static readonly Brush Muted = Frozen(Color.FromRgb(0x8C, 0x8C, 0x96));
    static readonly Brush Accent = Frozen(Color.FromRgb(0x5C, 0xE1, 0xFF));
    static readonly Brush CardBg = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    static readonly Brush CardSel = Frozen(Color.FromArgb(0x1F, 0x5C, 0xE1, 0xFF));
    static readonly Brush Border0 = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    readonly List<AgentInfo> agents;
    readonly Dictionary<string, (Border card, Ellipse dot)> cards = new();
    string? selected;

    public string? Chosen { get; private set; }

    public PickerWindow(string? current, bool firstRun)
    {
        InitializeComponent();
        Logo.Source = LoadLogo();
        if (!firstRun)
        {
            Subtitle.Text = "Choose which AI agent answers when you press Alt⁠+⁠A.";
            Hint.Text ="New and resumed sessions use the agent you pick.";
        }

        agents = Agents.Detect();
        foreach (var a in agents) List.Children.Add(MakeCard(a));

        var initial = agents.FirstOrDefault(a => a.Installed && a.Id == current) ?? agents.FirstOrDefault(a => a.Installed);
        if (initial != null) Select(initial.Id);
        else
        {
            UseBtn.IsEnabled = false;
            Hint.Text = "No supported agent found. Install one with the command shown, then reopen this window.";
        }

        Loaded += async (_, _) =>
        {
            foreach (var a in agents.Where(a => a.Installed))
            {
                var v = await Agents.VersionAsync(a.Path!);
                if (v != null && cards.TryGetValue(a.Id, out var c) && c.card.Tag is TextBlock meta)
                    meta.Text = $"{a.Vendor} · {v}";
            }
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    FrameworkElement MakeCard(AgentInfo a)
    {
        var dot = new Ellipse { Width = 14, Height = 14, Stroke = Muted, StrokeThickness = 1.5, VerticalAlignment = VerticalAlignment.Center };

        var name = new TextBlock { Text = a.Name, Foreground = Fg, FontSize = 15, FontWeight = FontWeights.SemiBold };
        var meta = new TextBlock { Text = a.Installed ? a.Vendor + " · checking version…" : a.Vendor, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        var body = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        body.Children.Add(name);
        body.Children.Add(meta);

        if (a.Installed)
            body.Children.Add(new TextBlock { Text = a.Path, Foreground = Muted, FontSize = 11, FontFamily = Mono, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        else
        {
            body.Children.Add(new TextBlock { Text = "Not installed. To install, run:", Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 6, 0, 2) });
            body.Children.Add(new TextBox
            {
                Text = a.InstallHint, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Foreground = Fg, FontFamily = Mono, FontSize = 12, Padding = new Thickness(0),
            });
        }

        var status = new TextBlock
        {
            Text = a.Installed ? "Installed" : "Not found",
            Foreground = a.Installed ? Accent : Muted, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 2, 0, 0),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(body, 1); Grid.SetColumn(status, 2);
        grid.Children.Add(dot); grid.Children.Add(body); grid.Children.Add(status);

        var card = new Border
        {
            Child = grid, Tag = meta, CornerRadius = new CornerRadius(12), Background = CardBg,
            BorderBrush = Border0, BorderThickness = new Thickness(1.5), Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 8), Opacity = a.Installed ? 1 : 0.6,
        };
        if (a.Installed)
        {
            card.Cursor = Cursors.Hand;
            card.MouseLeftButtonUp += (_, _) => Select(a.Id);
            card.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { Select(a.Id); Use_Click(this, new RoutedEventArgs()); } };
        }
        cards[a.Id] = (card, dot);
        return card;
    }

    void Select(string id)
    {
        selected = id;
        foreach (var (key, (card, dot)) in cards)
        {
            bool on = key == id;
            card.BorderBrush = on ? Accent : Border0;
            card.Background = on ? CardSel : CardBg;
            dot.Fill = on ? Accent : Brushes.Transparent;
            dot.Stroke = on ? Accent : Muted;
        }
        var name = agents.First(a => a.Id == id).Name;
        UseBtn.Content = $"Use {name}";
        UseBtn.IsEnabled = true;
    }

    void Use_Click(object sender, RoutedEventArgs e)
    {
        if (selected == null) return;
        Chosen = selected;
        Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    void Drag(object sender, MouseButtonEventArgs e) { try { DragMove(); } catch { } }

    static ImageSource? LoadLogo()
    {
        try
        {
            using var s = typeof(PickerWindow).Assembly.GetManifestResourceStream("computah.ico")!;
            var dec = new IconBitmapDecoder(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return dec.Frames.OrderByDescending(f => f.PixelWidth).First();
        }
        catch { return null; }
    }

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
