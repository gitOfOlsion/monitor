#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════════
# Peripheral Monitor Agent — Automated Linux Installer
# ═══════════════════════════════════════════════════════════════════════════
# - Creates a dedicated service account (pm-agent)
# - Installs the agent to /opt/peripheral-monitor (self-contained: no .NET needed)
# - Configures udev rules for real-time USB/input events
# - Registers and starts as a systemd service
# - Auto-starts after boot
#
# Usage: sudo bash install.sh [--dry-run]          install or upgrade in place
#        sudo bash install.sh --uninstall [--purge] remove (--purge also deletes event history)
#        sudo bash /opt/peripheral-monitor/uninstall.sh [--purge]
#
# Usually run for you by the one-file installer downloaded from the dashboard:
#        sudo sh PeripheralMonitorAgent-Setup-<institution>.run
# ═══════════════════════════════════════════════════════════════════════════

set -euo pipefail

INSTALL_DIR="/opt/peripheral-monitor"
SERVICE_USER="pm-agent"
SERVICE_NAME="peripheral-monitor-agent"
UDEV_PIPE_DIR="/run/peripheral-monitor"
# Event history (EventStore). Created by systemd via StateDirectory= in the unit below.
STATE_DIR="/var/lib/peripheralmonitor"

DRY_RUN=false
UNINSTALL=false
PURGE=false
[[ "$(basename "$0")" == "uninstall.sh" ]] && UNINSTALL=true
for arg in "$@"; do
    case "$arg" in
        --dry-run)   DRY_RUN=true ;;
        --uninstall) UNINSTALL=true ;;
        --purge)     PURGE=true ;;
        --quiet)     ;;   # accepted for symmetry with Windows; this script never prompts
        *) echo "Unknown option: $arg" >&2; exit 2 ;;
    esac
done

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
CYAN='\033[0;36m'
NC='\033[0m'

step()  { echo -e "\n${CYAN}[*] $*${NC}"; }
ok()    { echo -e "    ${GREEN}✓ $*${NC}"; }
warn()  { echo -e "    ${YELLOW}⚠ $*${NC}"; }
fail()  { echo -e "    ${RED}✗ $*${NC}"; }

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# ══════════════════════════════════════════════════════════════════════════
# STEP 0: Verify root
# ══════════════════════════════════════════════════════════════════════════

echo ""
echo "═══════════════════════════════════════════════════"
echo "  Peripheral Monitor Agent — Linux Installer"
echo "═══════════════════════════════════════════════════"

if [[ "$DRY_RUN" == true ]]; then
    step "Dry run — nothing will be changed"
    if [[ -f "$SCRIPT_DIR/appsettings.local.json" ]]; then
        ok "Server: $(grep -o '"SignalRUrl"[^,}]*' "$SCRIPT_DIR/appsettings.local.json" | cut -d'"' -f4)"
    else
        warn "No appsettings.local.json next to this script — an existing install keeps its config"
    fi
    [[ -f "$SCRIPT_DIR/PeripheralMonitor.Agent" ]] && ok "Agent binary present" || fail "Agent binary missing"
    command -v systemctl &>/dev/null && ok "systemd found" || fail "systemd not found (required)"
    echo "    Would: create user $SERVICE_USER, install to $INSTALL_DIR, add udev rules,"
    echo "    register $SERVICE_NAME.service, set up VNC (127.0.0.1 only) or SSH, and start it."
    exit 0
fi

if [[ $EUID -ne 0 ]]; then
    fail "This script must be run as root (sudo bash install.sh)"
    exit 1
fi
ok "Running as root"

if ! command -v systemctl &>/dev/null; then
    fail "systemd is required (systemctl not found)"
    exit 1
fi

# ── Uninstall ──────────────────────────────────────────────────────────────
if [[ "$UNINSTALL" == true ]]; then
    step "Uninstalling Peripheral Monitor Agent..."
    for unit in "$SERVICE_NAME" pm-x11vnc; do
        systemctl disable --now "$unit.service" 2>/dev/null || true
        rm -f "/etc/systemd/system/$unit.service"
    done
    systemctl daemon-reload
    ok "Services stopped and removed"

    rm -f /etc/udev/rules.d/99-peripheral-monitor.rules
    udevadm control --reload-rules 2>/dev/null || true
    rm -f /etc/tmpfiles.d/peripheral-monitor.conf /etc/sudoers.d/peripheral-monitor
    rm -rf "$UDEV_PIPE_DIR"
    ok "udev rule, tmpfiles entry and sudoers rule removed"

    rm -rf "$INSTALL_DIR"
    ok "$INSTALL_DIR removed"

    if id "$SERVICE_USER" &>/dev/null; then
        userdel "$SERVICE_USER" 2>/dev/null && ok "User $SERVICE_USER removed" || warn "Could not remove user $SERVICE_USER"
    fi

    if [[ "$PURGE" == true ]]; then
        rm -rf "$STATE_DIR"
        ok "Event history deleted ($STATE_DIR)"
    else
        echo "    Event history kept in $STATE_DIR (re-run with --purge to delete it)"
    fi
    echo "    Left in place: SSH and the x11vnc package, in case you use them for other things."
    echo ""
    ok "Uninstall complete"
    exit 0
fi

# Detect package manager
if command -v apt-get &>/dev/null; then
    PKG_MGR="apt"
elif command -v dnf &>/dev/null; then
    PKG_MGR="dnf"
elif command -v yum &>/dev/null; then
    PKG_MGR="yum"
elif command -v zypper &>/dev/null; then
    PKG_MGR="zypper"
else
    PKG_MGR="unknown"
fi
ok "Package manager: $PKG_MGR"

# ══════════════════════════════════════════════════════════════════════════
# STEP 1: Check agent binary / stop running agent
# ══════════════════════════════════════════════════════════════════════════

# The agent is published self-contained (see Server/Dockerfile), so it carries its
# own .NET runtime. Nothing to download here — the install also works offline.
step "Checking agent binary..."
if [[ ! -f "$SCRIPT_DIR/PeripheralMonitor.Agent" ]]; then
    fail "PeripheralMonitor.Agent not found next to this script"
    exit 1
fi
ok "Self-contained agent found (no .NET runtime needed)"

# Stop a running agent before replacing its binary (upgrade in place)
if systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; then
    systemctl stop "$SERVICE_NAME"
    ok "Stopped running agent for upgrade"
fi

# ══════════════════════════════════════════════════════════════════════════
# STEP 2: Create service account
# ══════════════════════════════════════════════════════════════════════════

step "Configuring service account '$SERVICE_USER'..."

if id "$SERVICE_USER" &>/dev/null; then
    ok "User '$SERVICE_USER' already exists"
else
    useradd \
        --system \
        --no-create-home \
        --home-dir "$INSTALL_DIR" \
        --shell /usr/sbin/nologin \
        --comment "Peripheral Monitor Agent" \
        "$SERVICE_USER"
    ok "System user '$SERVICE_USER' created (no login shell, no home)"
fi

# Add to groups needed for device monitoring
for grp in plugdev input dialout bluetooth; do
    if getent group "$grp" &>/dev/null; then
        usermod -aG "$grp" "$SERVICE_USER" 2>/dev/null || true
    fi
done
ok "Added to device groups: plugdev, input, dialout, bluetooth"

# Grant passwordless shutdown/reboot/cancel — required for remote restart/shutdown commands
cat > /etc/sudoers.d/peripheral-monitor << SUDOERS
# Peripheral Monitor Agent — allow remote restart/shutdown/cancel without a password
$SERVICE_USER ALL=(root) NOPASSWD: /sbin/shutdown, /usr/sbin/shutdown
SUDOERS
chmod 440 /etc/sudoers.d/peripheral-monitor
ok "Granted passwordless shutdown/reboot rights via /etc/sudoers.d/peripheral-monitor"

# ══════════════════════════════════════════════════════════════════════════
# STEP 3: Install agent files
# ══════════════════════════════════════════════════════════════════════════

step "Installing agent to $INSTALL_DIR..."

mkdir -p "$INSTALL_DIR"/{logs,data}

# Copy agent files (everything in same directory as this script, except installers).
# -mindepth 1: without it find also lists $SCRIPT_DIR itself, which copied the whole
# folder into a subfolder of $INSTALL_DIR.
find "$SCRIPT_DIR" -mindepth 1 -maxdepth 1 \
    ! -name "install.ps1" \
    ! -name "install.sh" \
    ! -name "uninstall.sh" \
    ! -name "*.service" \
    ! -name "*.rules" \
    -exec cp -r {} "$INSTALL_DIR/" \;

# Make the main binary executable
chmod +x "$INSTALL_DIR/PeripheralMonitor.Agent" 2>/dev/null || true

# Keep a copy of this script as the uninstaller
cp "$SCRIPT_DIR/install.sh" "$INSTALL_DIR/uninstall.sh"

ok "Agent files installed to $INSTALL_DIR"

# ══════════════════════════════════════════════════════════════════════════
# STEP 4: Set permissions
# ══════════════════════════════════════════════════════════════════════════

step "Setting permissions..."

chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR"
chmod -R 750 "$INSTALL_DIR"
# Config files should be readable only by service user
chmod 640 "$INSTALL_DIR"/appsettings*.json 2>/dev/null || true
# The uninstaller runs as root and must not be editable by the service account
chown root:root "$INSTALL_DIR/uninstall.sh"
chmod 700 "$INSTALL_DIR/uninstall.sh"

ok "Owner: $SERVICE_USER, mode: 750"

# ══════════════════════════════════════════════════════════════════════════
# STEP 5: Configure udev rules (real-time USB/input events)
# ══════════════════════════════════════════════════════════════════════════

step "Installing udev rules..."

mkdir -p "$UDEV_PIPE_DIR"
chown "$SERVICE_USER:$SERVICE_USER" "$UDEV_PIPE_DIR"

cat > /etc/udev/rules.d/99-peripheral-monitor.rules << 'UDEV'
# Peripheral Monitor — send device events to agent via named pipe
ACTION=="add",    SUBSYSTEM=="usb",       RUN+="/bin/sh -c 'echo add|$env{DEVPATH}|usb|$env{ID_MODEL} >> /run/peripheral-monitor/udev-events 2>/dev/null || true'"
ACTION=="remove", SUBSYSTEM=="usb",       RUN+="/bin/sh -c 'echo remove|$env{DEVPATH}|usb|$env{ID_MODEL} >> /run/peripheral-monitor/udev-events 2>/dev/null || true'"
ACTION=="add",    SUBSYSTEM=="input",     RUN+="/bin/sh -c 'echo add|$env{DEVPATH}|input|$env{ID_MODEL} >> /run/peripheral-monitor/udev-events 2>/dev/null || true'"
ACTION=="remove", SUBSYSTEM=="input",     RUN+="/bin/sh -c 'echo remove|$env{DEVPATH}|input|$env{ID_MODEL} >> /run/peripheral-monitor/udev-events 2>/dev/null || true'"
ACTION=="add",    SUBSYSTEM=="bluetooth", RUN+="/bin/sh -c 'echo add|$env{DEVPATH}|bluetooth|$env{ID_MODEL} >> /run/peripheral-monitor/udev-events 2>/dev/null || true'"
ACTION=="remove", SUBSYSTEM=="bluetooth", RUN+="/bin/sh -c 'echo remove|$env{DEVPATH}|bluetooth|$env{ID_MODEL} >> /run/peripheral-monitor/udev-events 2>/dev/null || true'"
UDEV

udevadm control --reload-rules 2>/dev/null || true
ok "udev rules installed and reloaded"

# Create tmpfiles.d entry so /run/peripheral-monitor survives reboots
cat > /etc/tmpfiles.d/peripheral-monitor.conf << TMPF
d $UDEV_PIPE_DIR 0755 $SERVICE_USER $SERVICE_USER -
TMPF
systemd-tmpfiles --create 2>/dev/null || true
ok "tmpfiles.d configured for $UDEV_PIPE_DIR"

# ══════════════════════════════════════════════════════════════════════════
# STEP 6: Create systemd service
# ══════════════════════════════════════════════════════════════════════════

step "Creating systemd service..."

cat > "/etc/systemd/system/${SERVICE_NAME}.service" << UNIT
[Unit]
Description=Peripheral Monitor Agent
After=network-online.target
Wants=network-online.target
StartLimitIntervalSec=300
StartLimitBurst=5

[Service]
Type=simple
User=$SERVICE_USER
Group=$SERVICE_USER
WorkingDirectory=$INSTALL_DIR
ExecStart=$INSTALL_DIR/PeripheralMonitor.Agent
Restart=always
RestartSec=10
TimeoutStopSec=30

# Security hardening
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=$INSTALL_DIR/logs $INSTALL_DIR/data $UDEV_PIPE_DIR
PrivateTmp=true
# Event history lives in $STATE_DIR. systemd creates it, owned by $SERVICE_USER, and
# makes it writable despite ProtectSystem=strict. Without this the agent could not
# create the folder, so the durable event history never worked on Linux.
StateDirectory=peripheralmonitor
StateDirectoryMode=0750

# Logging → journald (also goes to agent's own log files via Serilog)
StandardOutput=journal
StandardError=journal
SyslogIdentifier=pm-agent

# Environment (override via $INSTALL_DIR/appsettings.local.json instead)
# Environment=PM_SignalRUrl=https://monitor.yourdomain.com/monitor
# Environment=PM_JwtToken=TOKEN_HERE

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
ok "Systemd unit created: ${SERVICE_NAME}.service"


# ══════════════════════════════════════════════════════════════════════════
# STEP 7: Enable remote desktop access (for Guacamole web-based access)
# ══════════════════════════════════════════════════════════════════════════

step "Configuring remote desktop access..."

if ! command -v Xorg &>/dev/null && ! command -v Xwayland &>/dev/null && [[ ! -d /usr/share/xsessions ]]; then
    warn "No graphical desktop detected — installing SSH-only access (headless server)"
    # Ensure SSH is running for terminal-only Guacamole access
    if command -v systemctl &>/dev/null; then
        systemctl enable ssh 2>/dev/null || systemctl enable openssh-server 2>/dev/null || true
        systemctl start  ssh 2>/dev/null || systemctl start  openssh-server 2>/dev/null || true
    fi
    ok "SSH enabled for headless remote access via Guacamole"
else
    # Desktop machine — install x11vnc for Guacamole VNC access
    echo "    Desktop environment detected — setting up VNC (x11vnc)..."

    case "$PKG_MGR" in
        apt)
            apt-get install -y -qq x11vnc 2>/dev/null && ok "x11vnc installed" || warn "x11vnc install failed"
            ;;
        yum|dnf)
            $PKG_MGR install -y x11vnc 2>/dev/null && ok "x11vnc installed" || warn "x11vnc install failed"
            ;;
        *)
            warn "Unsupported package manager — install x11vnc manually for VNC access"
            ;;
    esac

    if command -v x11vnc &>/dev/null; then
        # Create systemd service for x11vnc that starts when a display is available.
        # SECURITY: -localhost binds VNC to 127.0.0.1 only. The agent's tunnel connects
        # locally, so nothing on the network ever needs to reach port 5900. Without it,
        # anyone on the LAN could take over the desktop (x11vnc has no password here).
        cat > /etc/systemd/system/pm-x11vnc.service << VNCSERVICE
[Unit]
Description=Peripheral Monitor x11vnc (VNC for Guacamole)
After=display-manager.service network.target
Wants=display-manager.service

[Service]
Type=simple
ExecStart=/usr/bin/x11vnc -localhost -display :0 -auth guess -forever -loop -noxdamage -repeat -rfbport 5900 -shared -capslock
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
VNCSERVICE
        systemctl daemon-reload
        systemctl enable pm-x11vnc.service 2>/dev/null || true
        # restart (not start) so a re-install replaces an older, network-exposed instance
        systemctl restart pm-x11vnc.service 2>/dev/null || true
        ok "x11vnc VNC service enabled (127.0.0.1:5900 only)"
    fi
fi

# ══════════════════════════════════════════════════════════════════════════
# STEP 8: Enable and start service
# ══════════════════════════════════════════════════════════════════════════

step "Enabling and starting service..."

systemctl enable "$SERVICE_NAME" --quiet
systemctl start  "$SERVICE_NAME"

sleep 3

if systemctl is-active --quiet "$SERVICE_NAME"; then
    ok "Service is running"
else
    STATUS=$(systemctl is-active "$SERVICE_NAME" 2>/dev/null || echo "unknown")
    warn "Service status: $STATUS"
    echo "    Check logs: journalctl -u $SERVICE_NAME -f"
    echo "    Or:         cat $INSTALL_DIR/logs/agent-*.log"
fi

# ══════════════════════════════════════════════════════════════════════════
# DONE
# ══════════════════════════════════════════════════════════════════════════

echo ""
echo -e "${GREEN}═══════════════════════════════════════════════════${NC}"
echo -e "${GREEN}  Installation Complete!${NC}"
echo -e "${GREEN}═══════════════════════════════════════════════════${NC}"
echo ""
echo "  Install path:  $INSTALL_DIR"
echo "  Service name:  $SERVICE_NAME"
echo "  Service user:  $SERVICE_USER"
echo "  Logs:          $INSTALL_DIR/logs/"
echo "  Config:        $INSTALL_DIR/appsettings.local.json"
echo "  udev rules:    /etc/udev/rules.d/99-peripheral-monitor.rules"
echo "  Remote desktop: VNC/SSH via the agent tunnel (VNC listens on 127.0.0.1 only)"
echo ""
echo "  Manage:"
echo "    Status:  systemctl status $SERVICE_NAME"
echo "    Logs:    journalctl -u $SERVICE_NAME -f"
echo "    Stop:    systemctl stop $SERVICE_NAME"
echo "    Start:   systemctl start $SERVICE_NAME"
echo "    Remove:  sudo bash $INSTALL_DIR/uninstall.sh   (add --purge to delete event history)"
echo ""
