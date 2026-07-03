# Peripheral Monitor

> Centralised, real-time peripheral device visibility across all your institutions.

![License](https://img.shields.io/badge/license-MIT-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)
![Platform](https://img.shields.io/badge/platform-Windows-blue)

---

## Overview

Peripheral Monitor is a multi-tenant, enterprise-grade device monitoring platform. It tracks USB, keyboard, mouse, audio, and other peripheral devices across multiple machines in real time — with no client UI required on monitored machines.

A lightweight Windows service (the **Agent**) runs silently on each monitored machine. It connects to a central **Server** via SignalR and reports device connect/disconnect events instantly. Administrators access a web-based **Dashboard** to view, approve, and manage agents across institutions.

---

## Features

- **Real-time peripheral tracking** — USB, keyboard, mouse, audio, HID, Bluetooth, and more
- **Multi-institution, multi-tenant** — each institution admin sees only their own devices
- **Device event history and audit log** — full connect/disconnect timeline per device
- **Web-based dashboard** — accessible from any browser, no client required
- **Agent approval workflow** — agents must be approved before reporting
- **JWT authentication** — signed tokens per agent, validated by the server
- **Session 0 compatible** — agent runs as a Windows service with no desktop required
- **Three-layer device detection** — SetupAPI snapshot + WMI + Win32 API for maximum reliability
- **Comparison view** — compare up to 3 devices simultaneously across institutions

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    Browser (any device)                      │
│              Blazor Server / Web Dashboard                   │
└───────────────────────┬─────────────────────────────────────┘
                        │ HTTP + WebSocket (SignalR)
┌───────────────────────▼─────────────────────────────────────┐
│                  ASP.NET Core Server                         │
│    AgentRegistry · UserStore · MonitorHub · REST API         │
└───────────┬───────────────────────────┬─────────────────────┘
            │ UDP Discovery             │ SignalR
┌───────────▼───────┐       ┌───────────▼───────────────────┐
│  Agent (Machine A) │       │      Agent (Machine B)         │
│  Windows Service   │       │      Windows Service           │
│  DeviceMonitorEngine       │      DeviceMonitorEngine       │
└───────────────────┘       └───────────────────────────────┘
```

---

## Project Structure

```
PeripheralMonitor/
├── Agent/                          # Windows service (runs on monitored machines)
│   ├── AgentWorker.cs              # Main background worker
│   ├── DeviceMonitorEngine.cs      # Three-layer device detection
│   ├── Program.cs
│   ├── Services/
│   │   └── DiscoveryService.cs     # UDP LAN discovery broadcaster
│   └── appsettings.json
│
├── Server/                         # ASP.NET Core server + Blazor dashboard
│   ├── Program.cs                  # Routes, auth, SignalR, Blazor config
│   ├── MonitorHub.cs               # SignalR hub
│   ├── Services/
│   │   ├── AgentRegistry.cs        # Agent state + persistence
│   │   ├── DiscoveryListener.cs    # UDP listener for agent discovery
│   │   ├── UserStore.cs            # Multi-tenant user management
│   │   └── AuthService.cs          # Cookie-based auth
│   ├── Pages/
│   │   ├── _Host.cshtml
│   │   ├── Login.razor
│   │   ├── Dashboard.razor
│   │   └── Admin.razor
│   └── Components/
│
├── Shared/                         # Shared models and utilities
│   ├── Models/
│   │   ├── AgentRegistration.cs
│   │   ├── DeviceEvent.cs
│   │   ├── ClientUser.cs
│   │   └── AgentStatus.cs
│   └── Security/
│       └── JwtHelper.cs            # JWT generation and validation
│
├── scripts/
│   └── install-service.ps1         # Agent installer PowerShell script
│
└── Client.Wpf/                     # Optional legacy WPF desktop client
```

---

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10/11 or Windows Server 2016+ (for the Agent)
- PowerShell 5.1 (for the installer script)

---

### 1. Run the server

```bash
cd Server
dotnet run
```

The server starts on `http://0.0.0.0:5000`. Navigate to `http://localhost:5000` in your browser.

Default admin credentials:
- **Username:** `admin`
- **Password:** `Admin@2024!`

> Change the default password immediately after first login.

---

### 2. Install the agent

On each machine to be monitored, run the installer as Administrator in **Windows PowerShell 5.1**:

```powershell
.\scripts\install-service.ps1 -ServerUrl "http://YOUR_SERVER_IP:5000"
```

Optional parameters:

| Parameter | Default | Description |
|---|---|---|
| `-ServerUrl` | `http://localhost:5000` | Server address |
| `-LogPath` | `C:\ProgramData\PeripheralMonitor\Logs` | Log file location |
| `-ServiceAccount` | `PMAgentSvc` | Dedicated Windows service account name |
| `-ServicePassword` | `PM@Agent2024!Secure` | Service account password |

The installer:
1. Creates a dedicated Windows service account (`PMAgentSvc`)
2. Adds it to the Administrators group (required for device event access)
3. Configures WMI `root\cimv2` permissions
4. Installs and starts the `PeripheralMonitorAgent` Windows service

---

### 3. Approve the agent

Once the agent starts, it broadcasts its presence via UDP. It appears as **Pending** in the dashboard within 60 seconds.

1. Open the dashboard at `http://YOUR_SERVER:5000`
2. Log in as admin
3. Find the machine under **Registered Agents**
4. Click **Approve**

The agent receives a JWT token, connects to the SignalR hub, and begins reporting device events immediately.

---

### 4. Grant access to institution clients

1. Log in as admin
2. Go to **Admin → User Management**
3. Create a new client account
4. Assign agents (by machine) to that client
5. Share the login URL with the client

Each client logs in and sees only their assigned machines.

---

## Configuration

### Server — `appsettings.json`

```json
{
  "Jwt": {
    "Key": "PeripheralMonitor_SecureKey_32Bytes_!!",
    "Issuer": "PeripheralMonitor",
    "Audience": "Agent"
  }
}
```

> The JWT key must be at least 32 characters. Change it before production deployment.

### Agent — `appsettings.json`

```json
{
  "SignalRUrl": "http://YOUR_SERVER:5000/monitor",
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning"
    }
  }
}
```

### Agent — `appsettings.local.json`

Written automatically after approval. Do not edit manually.

```json
{
  "SignalRUrl": "http://YOUR_SERVER:5000/monitor",
  "JwtToken": "<generated-jwt>"
}
```

---

## Device Detection — How It Works

The agent uses three layers to ensure no device event is ever missed:

| Layer | Mechanism | Latency | Session 0 |
|---|---|---|---|
| 1 | `RegisterDeviceNotification` (Win32 service handle) | < 1s | Yes |
| 2 | SetupAPI snapshot comparison (every 30s) | ≤ 30s | Yes |
| 3 | `ManagementEventWatcher` (WMI) | < 2s | Depends on WMI permissions |

Events from all three layers are de-duplicated — the same device event within 2 seconds is reported only once.

---

## Managing the service

```powershell
# Check status
Get-Service PeripheralMonitorAgent

# Start / stop / restart
Start-Service PeripheralMonitorAgent
Stop-Service PeripheralMonitorAgent
Restart-Service PeripheralMonitorAgent

# View logs (live)
Get-Content "C:\ProgramData\PeripheralMonitor\Logs\agent-.log" -Wait -Tail 50

# Uninstall
Stop-Service PeripheralMonitorAgent
sc.exe delete PeripheralMonitorAgent
```

---

## Production Deployment

### Behind nginx (recommended)

```nginx
server {
    listen 443 ssl;
    server_name monitordevice.com;

    ssl_certificate     /etc/ssl/certs/monitordevice.crt;
    ssl_certificate_key /etc/ssl/private/monitordevice.key;

    location / {
        proxy_pass         http://localhost:5000;
        proxy_http_version 1.1;

        # Required for SignalR WebSockets
        proxy_set_header   Upgrade $http_upgrade;
        proxy_set_header   Connection "upgrade";
        proxy_set_header   Host $host;
        proxy_cache_bypass $http_upgrade;
        proxy_read_timeout 100s;
        proxy_buffering    off;
    }
}
```

### Behind IIS

```xml
<system.webServer>
  <aspNetCore processPath="dotnet" arguments=".\Server.dll"
              stdoutLogEnabled="true" hostingModel="inprocess"/>
  <webSocket enabled="true"/>
</system.webServer>
```

---

## Security notes

- Change the default admin password immediately after first deployment
- Replace the JWT signing key in `appsettings.json` with a randomly generated 32+ character string
- Use HTTPS in production — the browser will warn about insecure logins over HTTP
- The `PMAgentSvc` service account has local administrator rights (required for WMI). Restrict its network access if needed
- Agent JWT tokens expire after 30 days — re-approve agents or implement token refresh for long-running deployments

---

## Troubleshooting

| Problem | Cause | Fix |
|---|---|---|
| Agent shows "approval timeout" | `appsettings.local.json` not found or empty token | Check path; write token manually via PowerShell |
| Agent shows 401 Unauthorized | JWT key mismatch between agent and server | Ensure both use the same key string |
| Agent shows 404 on `/monitor` | `SignalRUrl` missing `/monitor` suffix | Update `appsettings.json` with full hub URL |
| No device events in dashboard | WMI permissions not set | Re-run installer; check `wmimgmt.msc` |
| Agent not appearing in dashboard | UDP port 5001 blocked | Open UDP 5001 inbound on server firewall |
| CM_Register_Notification error 31 | Windows version or permission issue | Layer 1 skipped; Layers 2+3 handle detection |

---

## License

MIT — see [LICENSE](LICENSE) for details.

---

## Contributing

Pull requests are welcome. For major changes, open an issue first to discuss what you'd like to change.

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/your-feature`)
3. Commit your changes (`git commit -m 'Add your feature'`)
4. Push to the branch (`git push origin feature/your-feature`)
5. Open a pull request
