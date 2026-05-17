using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;                          // ✅ Fix #1: needed for FirstOrDefault
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;               // ✅ Fix #2: use proper Task namespace
using System.Windows;
using System.Windows.Controls;
using PeripheralMonitor.Shared.Models;
using PeripheralMonitor.Client.Wpf.Models;

namespace PeripheralMonitor.Client.Wpf;

public partial class MainWindow : Window
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://localhost:5000") };
    private readonly ObservableCollection<AgentRegistration> _agents = new();
    private readonly ObservableCollection<DeviceAlert> _alerts = new();
    //private AgentRegistration? _selectedAgent;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Status: Loading...";
        AgentsGrid.ItemsSource = _agents;
        EventsList.ItemsSource = _alerts;

        try
        {
            await LoadAgentsAsync();
            await ConnectSignalRAsync();
            StatusText.Text = "Status: Connected & Monitoring";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Status: Error - {ex.Message}";
        }
    }

    private async Task LoadAgentsAsync()
    {
        try
        {
            var agents = await _http.GetFromJsonAsync<List<AgentRegistration>>("/api/agents");
            if (agents != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _agents.Clear();
                    foreach (var agent in agents) _agents.Add(agent);
                });
            }
        }
        catch (Exception ex) { Console.WriteLine($"API Error: {ex.Message}"); }
    }

    private async Task ConnectSignalRAsync()
    {
        try
        {
            var hubConnection = new HubConnectionBuilder()
                .WithUrl("http://localhost:5000/monitor")
                .WithAutomaticReconnect()
                .Build();

            hubConnection.On<string, string, DateTime>("OnAgentStatus", (name, state, ts) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var agent = _agents.FirstOrDefault(a => a.MachineName == name);
                    if (agent != null)
                    {
                        var idx = _agents.IndexOf(agent);
                        var newStatus = state == "Online" ? AgentStatus.Approved : AgentStatus.Offline;

                        // ✅ 'with' works because AgentRegistration is a record
                        _agents.RemoveAt(idx);
                        _agents.Insert(idx, agent with { Status = newStatus });
                    }
                });
            });

            hubConnection.On<string, string, string>("OnDeviceChange", (name, type, json) =>
{
    Application.Current.Dispatcher.Invoke(() =>
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            
            // ✅ Must be FriendlyName — matches DeviceEvent record
            var deviceName = doc.RootElement.GetProperty("FriendlyName").GetString() ?? "Unknown";
            
            Console.WriteLine($"[SignalR] Device {type}: {deviceName} on {name}");
            
            var alert = new DeviceAlert(name, type, deviceName, DateTime.UtcNow.ToLocalTime());
            _alerts.Insert(0, alert);
            if (_alerts.Count > 100) _alerts.RemoveAt(100);

            //if (type == "Disconnected")
            //{
            //    //MessageBox.Show($"⚠️ {deviceName} disconnected on {name}",
            //       // "Device Alert", MessageBoxButton.OK, MessageBoxImage.Warning);
            //}
        }
        catch (Exception ex) 
        { 
            Console.WriteLine($"[SignalR] Parse error: {ex.Message}");
            Console.WriteLine($"[SignalR] Raw JSON: {json}"); // ✅ shows exact payload
        }
    });
});

            await hubConnection.StartAsync();
            Console.WriteLine("[SignalR] ✅ Connected");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SignalR] ❌ Failed: {ex.Message}");
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAgentsAsync();

    //private void AgentsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    //{
        //_selectedAgent = AgentsGrid.SelectedItem as AgentRegistration;
       // Console.WriteLine($"[Selection] Selected: {_selectedAgent?.MachineName ?? "null"}");
    //}

private async void Approve_Click(object sender, RoutedEventArgs e)
{
    // ✅ Read directly from grid at the moment of click
    if (AgentsGrid.SelectedItem is not AgentRegistration selected)
    {
        MessageBox.Show("Please select a row first.", "No Selection",
            MessageBoxButton.OK, MessageBoxImage.Information);
        return;
    }

    Console.WriteLine($"[Approve] {selected.MachineName} / {selected.MacAddress}");

    try
    {
        var mac = selected.MacAddress;
        var content = new StringContent("", Encoding.UTF8, "application/json");
        var url = "/api/agents/" + mac + "/approve?approvedBy=Admin";

        var response = await _http.PostAsync(url, content);
        Console.WriteLine($"[Approve] Response: {response.StatusCode}");

        if (response.IsSuccessStatusCode)
        {
            StatusText.Text = "Status: ✅ " + selected.MachineName + " approved";
            await LoadAgentsAsync();
        }
        else
        {
            var body = await response.Content.ReadAsStringAsync();
            MessageBox.Show($"Server returned {response.StatusCode}:\n{body}",
                "Approve Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Approve] Exception: {ex}");
        MessageBox.Show($"Error: {ex.Message}", "Exception",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
  }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        // ✅ Same pattern for Delete
        if (AgentsGrid.SelectedItem is not AgentRegistration selected)
        {
            MessageBox.Show("Please select a row first.", "No Selection",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show($"Delete {selected.MachineName}?", "Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            var response = await _http.DeleteAsync("/api/agents/" + selected.MacAddress);
            if (response.IsSuccessStatusCode) await LoadAgentsAsync();
        }
        catch (Exception ex) { MessageBox.Show($"Error: {ex.Message}"); }
    }

    private void ShowActivity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string machineName) return;

        // ✅ Debug — check what's in _alerts
        Console.WriteLine($"[Activity] Machine: '{machineName}'");
        Console.WriteLine($"[Activity] Total alerts: {_alerts.Count}");
        foreach (var a in _alerts.Take(3))
            Console.WriteLine($"[Activity]   → MachineName: '{a.MachineName}' DeviceName: '{a.DeviceName}'");

        var hasActivity = _alerts.Any(a => a.MachineName == machineName);
        Console.WriteLine($"[Activity] HasActivity: {hasActivity}");

        if (!hasActivity)
        {
            MessageBox.Show($"No activity recorded for {machineName} yet.",
                "No Activity", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new ActivityWindow(machineName, _alerts.ToList()) // ✅ ToList() snapshot
        {
            Owner = this
        };
        window.ShowDialog();
    }

}