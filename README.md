# Peripheral Monitor

> Centralised, real-time peripheral and endpoint monitoring across multiple institutions — with browser-based remote desktop, live system metrics, application control, and reporting.

![.NET](https://img.shields.io/badge/.NET-10.0-purple)
![Platform](https://img.shields.io/badge/agent-Windows%20%7C%20Linux-blue)
![Deploy](https://img.shields.io/badge/deploy-Docker%20Compose-2496ED)

---

## Overview

Peripheral Monitor is a multi-tenant device monitoring platform. A lightweight **Agent** runs as a service on each monitored machine and reports peripheral connect/disconnect events, live system metrics, and the logged-in user to a central **Server** over a persistent SignalR connection. Administrators use a web **Dashboard** to view devices, approve agents, open browser-based remote desktop sessions, manage per-institution/per-device features, and generate reports.

The platform runs as a set of Docker containers orchestrated with Docker Compose, behind a single Caddy reverse proxy.

---

## Architecture

The system is split into independently deployed tiers. **The web tier never touches the database directly** — it talks to the server tier over HTTP through a typed API client (`ServerApiClient`), and the server tier owns all data access.

```
                          Browser (desktop / mobile)
                                    │  HTTPS
                          ┌─────────▼──────────┐
                          │   Caddy (pm_caddy) │   reverse proxy + TLS
                          └───┬───────────┬────┘
             /  /api  /monitor           │  /remote/*   /seq/*
        ┌────────────▼─────────┐   ┌─────▼──────┐  ┌──────▼──────┐
        │  Web  (pm_web :5001) │   │ Guacamole  │  │  Seq (logs) │
        │  Blazor Server       │   │ (web RDP)  │  └─────────────┘
        └────────────┬─────────┘   └─────┬──────┘
                     │ HTTP (ServerApiClient)  │ RDP over tunnel
        ┌────────────▼──────────────────────────────┐
        │        Server (pm_server :5000)            │
        │  REST API · MonitorHub (SignalR) ·         │
        │  Metrics · Features · Auth · Reporting     │
        └───────┬───────────────────────────┬────────┘
                │ EF Core                    │ SignalR (reverse tunnel)
        ┌───────▼────────┐          ┌────────▼─────────────────┐
        │ PostgreSQL     │          │  Agents (Windows/Linux)   │
        │ (pm_db)        │          │  behind NAT, no inbound   │
        └────────────────┘          └───────────────────────────┘
```

### Containers

| Container | Role |
|---|---|
| `pm_web` | Blazor Server dashboard (port 5001) |
| `pm_server` | ASP.NET Core REST API + SignalR hub (port 5000) |
| `pm_db` | PostgreSQL 17 |
| `pm_caddy` | Reverse proxy + automatic TLS |
| `pm_guacamole` / `pm_guacd` | Apache Guacamole — browser-based remote desktop |
| `pm_seq` | Structured log viewer |

---

## Key features

- **Real-time peripheral tracking** — USB, keyboard, mouse, audio, HID, Bluetooth, storage, display and more, with a durable event history that survives agent restarts and reconnects.
- **Live system metrics** — per-device CPU, RAM, disk activity and network throughput, sampled from Windows performance counters (Task-Manager-equivalent) with live gauges and sparklines, organised by institution.
- **Logged-in user visibility** — the agent runs as LocalSystem and uses the Windows Terminal Services API to report the interactive user on each machine.
- **Browser-based remote desktop** — RDP through Apache Guacamole over a reverse SignalR tunnel, so agents behind NAT need no inbound ports.
- **Multi-tenant** — SuperAdmin sees everything; InstitutionAdmin sees only their own institution; Viewers are read-only.
- **Feature management (SuperAdmin)** — turn features (File Share, Remote Desktop, Restart/Shutdown, Reports, Application Control) on or off per institution or per device, with a two-tier override model (device → institution → default).
- **Application Control** — a block model that prevents the logged-in user from running disallowed file types / applications on the device (Windows enforcement via a SYSTEM process watcher; clean removal when disabled).
- **Reporting** — filter device events by institution, device, date range, category, event type and user, then print or save to PDF from the browser. Gated as a feature and hidden until enabled.
- **File share** — upload, download and delete files in a per-device shared folder.
- **Audit log** — every administrative action is recorded.
- **Mobile-friendly** — the dashboard is responsive, with an off-canvas navigation drawer on small screens.

---

## Technology

- **.NET 10** across Agent, Server, Web and Shared projects.
- **Blazor Server** for the dashboard (`pm_web`).
- **ASP.NET Core** REST API + **SignalR** hub for agent/dashboard realtime (`pm_server`).
- **PostgreSQL 17** via **EF Core** for persistence.
- **Apache Guacamole** for clientless remote desktop.
- **Docker Compose** for orchestration; **Caddy** for TLS and routing.
- **Serilog** → **Seq** for structured logging.

---

## Repository layout

```
PeripheralMonitor/
├── Agent/            # Cross-platform monitoring agent (Windows service / Linux daemon)
│   ├── AgentWorker.cs             # SignalR client, heartbeat, metrics sampling
│   ├── Services/
│   │   ├── EventStore.cs          # Durable JSONL outbox + append-only history
│   │   ├── FileShareService.cs    # Shared-folder upload/download/delete
│   │   └── AppControlEnforcer.cs  # Application Control enforcement
│   └── Installers/install.ps1     # Windows service installer (LocalSystem)
├── Server/           # REST API + SignalR hub + data access
│   ├── Controllers/  # Agents, Events, Institutions, Users, Features, Auth …
│   ├── Services/      # AgentService, MetricsService, FeatureService, UserService …
│   ├── Hubs/MonitorHub.cs
│   └── Data/AppDbContext.cs
├── Web/              # Blazor Server dashboard
│   ├── Components/Pages/           # Index (public), Dashboard, Agents, Metrics, Analytics, Reports, Features …
│   └── Services/ServerApiClient.cs # The web tier's only path to the server
├── Shared/           # DTOs and models shared by all projects
├── Caddy/            # Reverse-proxy configuration
├── Guacamole/        # Remote desktop gateway configuration
└── docker-compose.yml
```

---

## Getting started

### Prerequisites

- A Linux host with **Docker** and **Docker Compose**.
- Ports 80/443 reachable (for Caddy/TLS) if deploying with a domain.
- For agents: Windows 10/11 or Windows Server (primary), or Linux (best-effort).

### Run the stack

```bash
git clone <your-repo> PeripheralMonitor
cd PeripheralMonitor
docker compose up -d --build
```

The public landing page is served at the site root; the authenticated dashboard lives at `/dashboard` after sign-in. In development Caddy listens on port 80; for production, point a domain at the host and Caddy provisions TLS automatically (see `Caddy/Caddyfile`).

### First login

Sign in with the seeded SuperAdmin account, then create institutions and users from the dashboard. Change the seeded password immediately.

### Install an agent

1. In the dashboard, open an institution and choose **Download Agent → Windows installer (.exe)**. The file is pre-configured with the institution's enrolment token.
2. On the target machine, double-click it and accept the administrator prompt. The agent installs as the `PeripheralMonitorAgent` service running as **LocalSystem** (required for Terminal Services session queries) and begins reporting. It appears in **Apps & features**, where it can be uninstalled.
3. Approve the machine in the dashboard if it appears as pending.

**Mass deployment (GPO, Intune, scripts):**

```powershell
PeripheralMonitorAgent-Setup-<institution>.exe install --quiet     # unattended install
PeripheralMonitorAgent-Setup-<institution>.exe install --dry-run   # show what it would do
"C:\PeripheralMonitor\PeripheralMonitor.Agent.exe" uninstall --quiet [--purge]
```

Running the same installer again upgrades in place and keeps the event history. Exit codes: `0` ok, `1` failed, `2` no institution config, `3` admin rights declined.

How it works: the agent `.exe` is its own installer. When you download it, the server appends the institution's settings to the end of the stock `.exe` (`InstallerConfigTrailer`), so every download is personalised without a rebuild. A `.zip` with `install.ps1` is still available for script-based installs.

**Linux** — choose **Download Agent → Linux installer (.run)**, then:

```bash
sudo sh PeripheralMonitorAgent-Setup-<institution>.run              # install or upgrade
sh PeripheralMonitorAgent-Setup-<institution>.run --dry-run         # check, change nothing
sudo bash /opt/peripheral-monitor/uninstall.sh [--purge]            # remove
```

The `.run` file is a short shell script followed by a `.tar.gz` holding the agent, `install.sh` and the institution's config. The server generates it on the fly (`AgentPackageService.WriteLinuxInstallerAsync`). It needs systemd, bash and tar, and works offline: the agent is self-contained, so no .NET runtime is installed.

**Security defaults:** the install folder and `C:\ProgramData\PeripheralMonitor` are readable only by SYSTEM and Administrators. RDP is enabled for the agent tunnel but **not** opened in the firewall (the tunnel connects to `127.0.0.1`). On Linux, x11vnc listens on `127.0.0.1` only. Pass `--allow-lan-rdp` (`-AllowLanRdp` for the script) only if you also want direct RDP from the LAN.

---

## How some pieces work

### Durable event delivery

The agent writes every event to two JSONL files under `C:\ProgramData\PeripheralMonitor\` (Windows) or `/var/lib/peripheralmonitor/` (Linux): an **outbox** that is drained as events are acknowledged, and an append-only **history** that is never deleted. Each event carries a client-generated GUID; the server enforces a unique index so reconnects and retries never create duplicates.

### Live metrics

On Windows the agent reads `Win32_PerfFormattedData_*` performance counters (the same source Task Manager uses) for CPU, disk activity and network throughput, plus memory from the OS. The server keeps a short rolling window per device to drive the live gauges and sparklines on the Metrics dashboard, which groups devices by institution.

### Remote desktop without inbound ports

Agents hold an outbound SignalR connection to the server. Remote desktop traffic is tunnelled back over that connection to Guacamole, so machines behind NAT/firewalls need no inbound rules.

### Feature flags

Features resolve in three tiers: an explicit **device** override wins over an **institution** override, which wins over the feature's **catalog default**. Only SuperAdmin manages flags; enforcement is applied server-side on every gated endpoint (not merely hidden in the UI). Feature-gated pages such as Reports stay hidden for an institution until enabled.

### Application Control

When enabled for a device, the agent enforces a block-list policy (blocked extensions, blocked name patterns, allowed-pattern exceptions, optional installer blocking). On Windows a SYSTEM process watcher terminates disallowed processes shortly after launch; a hard protected-process list ensures system, shell and agent processes are never touched. Turning the feature off removes enforcement cleanly.

---

## Managing the agent (Windows)

```powershell
Get-Service PeripheralMonitorAgent
Restart-Service PeripheralMonitorAgent
Get-Content "C:\PeripheralMonitor\logs\agent-$(Get-Date -Format 'yyyyMMdd').log" -Tail 50 -Wait
```

---

## Security notes

- The agent runs as **LocalSystem** on Windows — required for Terminal Services session queries and Application Control enforcement.
- Remote desktop credentials are stored encrypted.
- Change all seeded credentials before production use, and deploy behind HTTPS (Caddy provisions this automatically when a domain is configured).
- Feature enforcement is server-side; disabling a feature blocks its endpoints regardless of the client.

---

## License

Proprietary — all rights reserved.
