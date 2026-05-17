using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.AspNetCore.SignalR.Client;
using PeripheralMonitor.Shared.Models;
using PeripheralMonitor.Client.Wpf.Models;

namespace PeripheralMonitor.Client.Wpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private HubConnection _hub = null!;
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://localhost:5000") };
    private readonly Dispatcher _dispatcher;

    [ObservableProperty] private ObservableCollection<AgentRegistration> _agents = new();
    [ObservableProperty] private ObservableCollection<DeviceAlert> _alerts = new();
    [ObservableProperty] private AgentRegistration? _selectedAgent;

    public MainViewModel()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        InitializeHub();
        _ = LoadAgentsAsync();
    }

    private void InitializeHub()
    {
        var serverUrl = "http://localhost:5000/monitor";
        Debug.WriteLine($"[DASHBOARD] Initializing SignalR connection to: {serverUrl}");

        _hub = new HubConnectionBuilder()
            .WithUrl(serverUrl, options =>
            {
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets 
                                   | Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10) })
            .Build();

        // ✅ CORRECT SignalR Connection Events (v8 API)
        _hub.Reconnecting += error =>
        {
            Debug.WriteLine($"[DASHBOARD] 🔄 Reconnecting... {(error?.Message ?? "Unknown")}");
            return Task.CompletedTask;
        };

        _hub.Reconnected += id =>
        {
            Debug.WriteLine($"[DASHBOARD] ✅ Reconnected! ConnectionId: {id}");
            return Task.CompletedTask;
        };

        _hub.Closed += error =>
        {
            Debug.WriteLine($"[DASHBOARD] ❌ Connection closed: {(error?.Message ?? "Clean exit")}");
            return Task.CompletedTask;
        };

        // ✅ Message Handlers
        _hub.On<string, string, DateTime>("OnAgentStatus", (name, state, ts) =>
        {
            Debug.WriteLine($"[DASHBOARD] 📡 Received OnAgentStatus: {name} is {state}");
            _dispatcher.InvokeAsync(() => UpdateAgentStatus(name, state, ts));
        });

        _hub.On<string, string, string>("OnDeviceChange", (name, type, json) =>
        {
            Debug.WriteLine($"[DASHBOARD] 🔔 Received OnDeviceChange: {type} on {name}");
            _dispatcher.InvokeAsync(() => HandleDeviceChange(name, type, json));
        });

        _ = StartHubAsync();
    }

    private async Task StartHubAsync()
    {
        try
        {
            Debug.WriteLine("[DASHBOARD] 🚀 Starting SignalR connection...");
            await _hub.StartAsync();
            Debug.WriteLine($"[DASHBOARD] ✅ Connected! State: {_hub.State}, ID: {_hub.ConnectionId}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DASHBOARD] ❌ Connection failed: {ex.Message}");
        }
    }

    private async Task LoadAgentsAsync()
    {
        try
        {
            Debug.WriteLine("[DASHBOARD] 📥 Fetching agents from API...");
            var response = await _http.GetFromJsonAsync<List<AgentRegistration>>("/api/agents");
            if (response != null)
            {
                _dispatcher.Invoke(() =>
                {
                    Agents.Clear();
                    foreach (var agent in response) Agents.Add(agent);
                    Debug.WriteLine($"[DASHBOARD] 📊 Loaded {Agents.Count} agents from API");
                });
            }
        }
        catch (Exception ex) 
        { 
            Debug.WriteLine($"[DASHBOARD] ⚠️ API load failed: {ex.Message}"); 
        }
    }

    [RelayCommand(CanExecute = nameof(CanApproveAgent))]
    private async Task ApproveAgentAsync()
    {
        if (SelectedAgent?.MacAddress == null) return;
        try
        {
            var content = new StringContent("", Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(
                $"/api/agents/{SelectedAgent.MacAddress}/approve?approvedBy=Admin", content);
            
            if (response.IsSuccessStatusCode)
            {
                ShowToastAlert($"✅ Approved {SelectedAgent.MachineName}");
                await LoadAgentsAsync();
            }
        }
        catch (Exception ex) { ShowToastAlert($"❌ Approval failed: {ex.Message}"); }
    }
    private bool CanApproveAgent() => SelectedAgent?.Status == AgentStatus.Pending;

    [RelayCommand(CanExecute = nameof(CanDeleteAgent))]
    private async Task DeleteAgentAsync()
    {
        if (SelectedAgent?.MacAddress == null) return;
        var result = MessageBox.Show($"Delete unapproved agent '{SelectedAgent.MachineName}'?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            var response = await _http.DeleteAsync($"/api/agents/{SelectedAgent.MacAddress}");
            if (response.IsSuccessStatusCode)
            {
                ShowToastAlert($"🗑️ Deleted {SelectedAgent.MachineName}");
                await LoadAgentsAsync();
            }
        }
        catch (Exception ex) { ShowToastAlert($"❌ Delete failed: {ex.Message}"); }
    }
    private bool CanDeleteAgent() => SelectedAgent?.Status != AgentStatus.Approved;

    partial void OnSelectedAgentChanged(AgentRegistration? value)
    {
        ApproveAgentCommand.NotifyCanExecuteChanged();
        DeleteAgentCommand.NotifyCanExecuteChanged();
    }

    private void UpdateAgentStatus(string name, string state, DateTime ts)
    {
        var idx = Agents.ToList().FindIndex(a => a.MachineName == name);
        if (idx >= 0)
        {
            var newStatus = state == "Online" ? AgentStatus.Approved : AgentStatus.Offline;
            Agents[idx] = Agents[idx] with { Status = newStatus };
        }
        else if (!Agents.Any(a => a.MachineName == name))
        {
            Agents.Add(new AgentRegistration(name, "Unknown", "Unknown", ts, AgentStatus.Approved, "Auto", DateTime.UtcNow, null));
        }
    }

    private void HandleDeviceChange(string machine, string type, string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var deviceName = doc.RootElement.GetProperty("FriendlyName").GetString() ?? "Unknown";
            var alert = new DeviceAlert(machine, type, deviceName, DateTime.UtcNow);
            Alerts.Insert(0, alert);
            if (Alerts.Count > 200) Alerts.RemoveAt(200);
            if (type == "Disconnected") ShowToastAlert($"⚠️ {deviceName} disconnected on {machine}");
        }
        catch (Exception ex) { Debug.WriteLine($"⚠️ Parse error: {ex.Message}"); }
    }

    private void ShowToastAlert(string message)
    {
        _dispatcher.Invoke(() =>
        {
            var popup = new System.Windows.Controls.Primitives.Popup
            {
                IsOpen = true, PlacementTarget = Application.Current.MainWindow,
                Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
                Child = new System.Windows.Controls.Border
                {
                    Background = System.Windows.Media.Brushes.Crimson, CornerRadius = new(4),
                    Padding = new(12, 8, 12, 8),
                    Child = new System.Windows.Controls.TextBlock { Text = message, Foreground = System.Windows.Media.Brushes.White, FontSize = 13 }
                }
            };
            popup.IsOpen = true;
            _ = Task.Delay(4000).ContinueWith(_ => _dispatcher.Invoke(() => popup.IsOpen = false));
        });
    }
}