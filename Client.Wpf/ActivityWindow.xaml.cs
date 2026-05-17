using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PeripheralMonitor.Client.Wpf.Models;

namespace PeripheralMonitor.Client.Wpf;

public class DeviceActivityRow
{
    public string   Category         { get; set; } = "";
    public string   DeviceName       { get; set; } = "";
    public string   LastEvent        { get; set; } = "";
    public DateTime LastSeen         { get; set; }
    public int      TotalEvents      { get; set; }
    public int      ConnectedCount   { get; set; }
    public int      DisconnectedCount{ get; set; }
    public List<DeviceAlert> Events  { get; set; } = new();
}

public partial class ActivityWindow : Window
{
    private readonly string            _machineName;
    private readonly List<DeviceAlert> _allAlerts;
    private List<DeviceActivityRow>    _grouped = new();
    private bool                       _isLoaded = false;

    public ActivityWindow(string machineName, List<DeviceAlert> alerts)
    {
        InitializeComponent();

        _machineName = machineName;
        _allAlerts   = alerts;

        TitleText.Text    = "📋 Device Activity — " + machineName;
        SubtitleText.Text = alerts.Count + " total events recorded";

        _isLoaded = true;
        LoadData();
    }

    // ── Categorise raw device name into friendly group ──────────────────
    private static string GetCategory(string deviceName)
    {
        var n = deviceName.ToLower();
        if (n.Contains("keyboard"))                      return "⌨️  Keyboard";
        if (n.Contains("mouse") || n.Contains("hid-compliant pointer")) return "🖱️  Mouse";
        if (n.Contains("composite") || n.Contains("input device"))       return "🔌 USB Device";
        if (n.Contains("audio") || n.Contains("speaker") || n.Contains("headset")) return "🔊 Audio";
        if (n.Contains("display") || n.Contains("monitor"))              return "🖥️  Display";
        if (n.Contains("storage") || n.Contains("disk") || n.Contains("flash")) return "💾 Storage";
        if (n.Contains("system controller") || n.Contains("consumer control")) return "🎮 Controller";
        if (n.Contains("bluetooth"))                     return "📶 Bluetooth";
        if (n.Contains("webcam") || n.Contains("camera")) return "📷 Camera";
        return "🔧 Other";
    }

    // ── Load & group data ────────────────────────────────────────────────
    private void LoadData()
    {
        _grouped = _allAlerts
            .Where(a => a.MachineName == _machineName)
            .GroupBy(a => a.DeviceName)
            .Select(g =>
            {
                var ordered = g.OrderByDescending(x => x.Timestamp).ToList();
                return new DeviceActivityRow
                {
                    Category          = GetCategory(g.Key),
                    DeviceName        = g.Key,
                    LastEvent         = ordered[0].EventType,
                    LastSeen          = ordered[0].Timestamp,
                    TotalEvents       = g.Count(),
                    ConnectedCount    = g.Count(x => x.EventType == "Connected"),
                    DisconnectedCount = g.Count(x => x.EventType == "Disconnected"),
                    Events            = ordered
                };
            })
            .OrderBy(r => r.Category)
            .ThenByDescending(r => r.LastSeen)
            .ToList();

        ApplyFilters();
        LoadConnectedDevices();
    }

    // ── Build TreeView ───────────────────────────────────────────────────
    private void ApplyFilters()
    {
        if (!_isLoaded) return;

        var filterText  = FilterBox?.Text?.ToLower() ?? "";
        var eventFilter = "All Events";
        if (EventTypeFilter?.SelectedItem is ComboBoxItem sel)
            eventFilter = sel.Content?.ToString() ?? "All Events";

        var filtered = _grouped
            .Where(r => string.IsNullOrEmpty(filterText) ||
                        r.DeviceName.ToLower().Contains(filterText) ||
                        r.Category.ToLower().Contains(filterText))
            .Where(r => eventFilter == "All Events" || r.LastEvent == eventFilter)
            .ToList();

        // Group by category for the tree
        var byCategory = filtered.GroupBy(r => r.Category);

        ActivityTree.Items.Clear();

        foreach (var cat in byCategory)
        {
            // ── Category node ──
            var catNode = new TreeViewItem
            {
                IsExpanded = true,
                Padding    = new Thickness(4, 6, 4, 6)
            };

            var catHeader = new StackPanel { Orientation = Orientation.Horizontal };
            catHeader.Children.Add(new TextBlock
            {
                Text       = cat.Key + "  (" + cat.Count() + " device(s))",
                FontWeight = FontWeights.Bold,
                FontSize   = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(26, 26, 46))
            });
            catNode.Header = catHeader;

            foreach (var device in cat.OrderByDescending(d => d.LastSeen))
            {
                // ── Device node (summary row, clickable to expand) ──
                var lastEventColor = device.LastEvent == "Connected"
                    ? Color.FromRgb(46, 125, 50)
                    : Color.FromRgb(198, 40, 40);

                var deviceHeader = new Grid { Width = 750 };
                deviceHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
                deviceHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                deviceHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
                deviceHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var nameBlock = new TextBlock
                {
                    Text         = device.DeviceName,
                    FontWeight   = FontWeights.SemiBold,
                    ToolTip      = device.DeviceName,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };

                var statusBlock = new TextBlock
                {
                    Text       = device.LastEvent,
                    Foreground = new SolidColorBrush(lastEventColor),
                    FontWeight = FontWeights.SemiBold,
                    Margin     = new Thickness(8, 0, 0, 0)
                };

                var timeBlock = new TextBlock
                {
                    Text       = device.LastSeen.ToString("yyyy-MM-dd HH:mm:ss"),
                    Foreground = new SolidColorBrush(Colors.Gray),
                    Margin     = new Thickness(8, 0, 0, 0)
                };

                var countBlock = new TextBlock
                {
                    Text       = "🔗 " + device.ConnectedCount + "  ✖ " + device.DisconnectedCount,
                    Foreground = new SolidColorBrush(Colors.Gray),
                    FontSize   = 11,
                    Margin     = new Thickness(8, 0, 0, 0)
                };

                Grid.SetColumn(nameBlock,   0);
                Grid.SetColumn(statusBlock, 1);
                Grid.SetColumn(timeBlock,   2);
                Grid.SetColumn(countBlock,  3);

                deviceHeader.Children.Add(nameBlock);
                deviceHeader.Children.Add(statusBlock);
                deviceHeader.Children.Add(timeBlock);
                deviceHeader.Children.Add(countBlock);

                var deviceNode = new TreeViewItem
                {
                    Header     = deviceHeader,
                    IsExpanded = false,
                    Padding    = new Thickness(4, 3, 4, 3)
                };

                // ── Event detail rows (shown when expanded) ──
                foreach (var evt in device.Events)
                {
                    var evtColor = evt.EventType == "Connected"
                        ? Color.FromRgb(46, 125, 50)
                        : Color.FromRgb(198, 40, 40);

                    var evtPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 2, 0, 2) };
                    evtPanel.Children.Add(new TextBlock
                    {
                        Text       = evt.EventType == "Connected" ? "▶  " : "■  ",
                        Foreground = new SolidColorBrush(evtColor)
                    });
                    evtPanel.Children.Add(new TextBlock
                    {
                        Text       = evt.EventType,
                        Foreground = new SolidColorBrush(evtColor),
                        Width      = 100,
                        FontWeight = FontWeights.SemiBold
                    });
                    evtPanel.Children.Add(new TextBlock
                    {
                        Text       = evt.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                        Foreground = new SolidColorBrush(Colors.Gray),
                        Margin     = new Thickness(8, 0, 0, 0)
                    });

                    deviceNode.Items.Add(new TreeViewItem { Header = evtPanel });
                }

                catNode.Items.Add(deviceNode);
            }

            ActivityTree.Items.Add(catNode);
        }

        CountText.Text = filtered.Count + " device(s) · " + filtered.Sum(r => r.TotalEvents) + " events";
    }

    // ── Connected Devices tab ────────────────────────────────────────────
    private void LoadConnectedDevices()
    {
        // A device is "connected" if its last event was Connected
        var connected = _grouped
            .Where(r => r.LastEvent == "Connected")
            .OrderBy(r => r.Category)
            .ToList();

        ConnectedGrid.ItemsSource = connected;
    }

    // ── Tab switching ────────────────────────────────────────────────────
    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        if (TabActivity?.IsChecked == true)
        {
            PanelActivity.Visibility  = Visibility.Visible;
            PanelConnected.Visibility = Visibility.Collapsed;
        }
        else
        {
            PanelActivity.Visibility  = Visibility.Collapsed;
            PanelConnected.Visibility = Visibility.Visible;
        }
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilters();
    private void EventTypeFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilters();
    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadData();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}