#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════════
# Peripheral Monitor Agent — Automated Linux Installer
# ═══════════════════════════════════════════════════════════════════════════
# - Checks/installs .NET 10 Runtime
# - Creates a dedicated service account (pm-agent)
# - Installs the agent to /opt/peripheral-monitor
# - Configures udev rules for real-time USB/input events
# - Registers and starts as a systemd service
# - Auto-starts after boot
#
# Usage: sudo bash install.sh
# ═══════════════════════════════════════════════════════════════════════════

set -euo pipefail

INSTALL_DIR="/opt/peripheral-monitor"
SERVICE_USER="pm-agent"
SERVICE_NAME="peripheral-monitor-agent"
DOTNET_VERSION="10.0"
DOTNET_MAJOR="${DOTNET_VERSION%%.*}"
UDEV_PIPE_DIR="/run/peripheral-monitor"

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

if [[ $EUID -ne 0 ]]; then
    fail "This script must be run as root (sudo bash install.sh)"
    exit 1
fi
ok "Running as root"

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
# STEP 1: Check/Install .NET Runtime
# ══════════════════════════════════════════════════════════════════════════

step "Checking .NET Runtime..."

dotnet_ok=false
if command -v dotnet &>/dev/null; then
    INSTALLED=$(dotnet --list-runtimes 2>/dev/null | grep "Microsoft.NETCore.App" | grep -oP '\d+' | head -1 || true)
    if [[ -n "$INSTALLED" ]] && [[ "$INSTALLED" -ge "$DOTNET_MAJOR" ]]; then
        ok ".NET ${INSTALLED}.x runtime found"
        dotnet_ok=true
    fi
fi

if [[ "$dotnet_ok" == false ]]; then
    warn ".NET $DOTNET_VERSION runtime not found — installing..."

    case "$PKG_MGR" in
        apt)
            # Microsoft package repository
            apt-get update -qq
            apt-get install -y -qq wget apt-transport-https

            # Add Microsoft package signing key and repo
            wget -q "https://packages.microsoft.com/config/$(. /etc/os-release && echo "$ID/$VERSION_ID")/packages-microsoft-prod.deb" \
                 -O /tmp/packages-microsoft-prod.deb 2>/dev/null || true

            if [[ -f /tmp/packages-microsoft-prod.deb ]]; then
                dpkg -i /tmp/packages-microsoft-prod.deb
                rm -f /tmp/packages-microsoft-prod.deb
                apt-get update -qq
                apt-get install -y -qq "dotnet-runtime-${DOTNET_VERSION}" "aspnetcore-runtime-${DOTNET_VERSION}"
            else
                warn "Microsoft repo not available for this distro — using install script"
                wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
                bash /tmp/dotnet-install.sh --channel "$DOTNET_VERSION" --runtime aspnetcore --install-dir /usr/share/dotnet
                bash /tmp/dotnet-install.sh --channel "$DOTNET_VERSION" --runtime dotnet     --install-dir /usr/share/dotnet
                ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
                rm -f /tmp/dotnet-install.sh
            fi
            ;;
        dnf|yum)
            $PKG_MGR install -y -q "dotnet-runtime-${DOTNET_VERSION}" "aspnetcore-runtime-${DOTNET_VERSION}" 2>/dev/null || {
                warn "Package not available — using install script"
                wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
                bash /tmp/dotnet-install.sh --channel "$DOTNET_VERSION" --runtime aspnetcore --install-dir /usr/share/dotnet
                bash /tmp/dotnet-install.sh --channel "$DOTNET_VERSION" --runtime dotnet     --install-dir /usr/share/dotnet
                ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
                rm -f /tmp/dotnet-install.sh
            }
            ;;
        *)
            warn "Unknown package manager — using .NET install script"
            wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
            bash /tmp/dotnet-install.sh --channel "$DOTNET_VERSION" --runtime aspnetcore --install-dir /usr/share/dotnet
            bash /tmp/dotnet-install.sh --channel "$DOTNET_VERSION" --runtime dotnet     --install-dir /usr/share/dotnet
            ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
            rm -f /tmp/dotnet-install.sh
            ;;
    esac

    # Verify installation
    if command -v dotnet &>/dev/null; then
        ok ".NET runtime installed: $(dotnet --version 2>/dev/null || echo 'unknown')"
    else
        fail ".NET installation failed"
        echo "    Download manually: https://dotnet.microsoft.com/download/dotnet/$DOTNET_VERSION"
        exit 1
    fi
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

# Copy agent files (everything in same directory as this script, except installers)
find "$SCRIPT_DIR" -maxdepth 1 \
    ! -name "install.ps1" \
    ! -name "install.sh" \
    ! -name "*.service" \
    ! -name "*.rules" \
    ! -name "." \
    -exec cp -r {} "$INSTALL_DIR/" \;

# Make the main binary executable
chmod +x "$INSTALL_DIR/PeripheralMonitor.Agent" 2>/dev/null || true

ok "Agent files installed to $INSTALL_DIR"

# ══════════════════════════════════════════════════════════════════════════
# STEP 4: Set permissions
# ══════════════════════════════════════════════════════════════════════════

step "Setting permissions..."

chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR"
chmod -R 750 "$INSTALL_DIR"
# Config files should be readable only by service user
chmod 640 "$INSTALL_DIR"/appsettings*.json 2>/dev/null || true

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
# STEP 7: Install and configure RustDesk (remote desktop)
# ══════════════════════════════════════════════════════════════════════════

# ══════════════════════════════════════════════════════════════════════════
# STEP 7: Install and configure RustDesk (remote desktop)
# ══════════════════════════════════════════════════════════════════════════
# Checklist (same pattern as the .NET runtime check above):
#   1. Is RustDesk installed at all?            -> if not, download + install
#   2. Is the installed version recent enough?  -> if not, upgrade
#   3. Is it already pointed at OUR server?      -> if not, reconfigure
#      (always re-check config, even if already installed, in case it was
#       previously configured for a different relay/server)
# ══════════════════════════════════════════════════════════════════════════

step "Checking RustDesk (remote desktop)..."

RD_MIN_VERSION="1.2.0"

RD_SERVER_ADDR=""
RD_PASSWORD=""
if [[ -f "$INSTALL_DIR/appsettings.local.json" ]]; then
    RD_SERVER_ADDR=$(grep -A2 '"RustDesk"' "$INSTALL_DIR/appsettings.local.json" | grep -oP '"ServerAddr"\s*:\s*"\K[^"]*' || true)
    RD_PASSWORD=$(grep -A2 '"RustDesk"' "$INSTALL_DIR/appsettings.local.json" | grep -oP '"Password"\s*:\s*"\K[^"]*' || true)
fi

version_ge() {
    # returns 0 (true) if $1 >= $2
    [[ "$1" == "$2" ]] && return 0
    [[ "$(printf '%s\n%s' "$1" "$2" | sort -V | head -n1)" == "$2" ]]
}

if [[ -z "$RD_SERVER_ADDR" ]]; then
    warn "No RustDesk server configured in appsettings.local.json — skipping remote desktop setup"
elif ! command -v Xorg &>/dev/null && ! command -v Xwayland &>/dev/null && [[ ! -d /usr/share/xsessions ]]; then
    warn "No graphical desktop environment detected — RustDesk requires a GUI session, skipping"
    echo "    Device monitoring will continue to work normally on this headless machine."
else
    RD_INSTALLED=false
    RD_NEEDS_UPGRADE=false

    if command -v rustdesk &>/dev/null; then
        RD_INSTALLED=true
        RD_CURRENT_VERSION=$(rustdesk --version 2>/dev/null | grep -oP '\d+\.\d+\.\d+' | head -1 || echo "0.0.0")
        if version_ge "$RD_CURRENT_VERSION" "$RD_MIN_VERSION"; then
            ok "RustDesk $RD_CURRENT_VERSION already installed (meets minimum $RD_MIN_VERSION)"
        else
            warn "RustDesk $RD_CURRENT_VERSION is older than required $RD_MIN_VERSION — will upgrade"
            RD_NEEDS_UPGRADE=true
        fi
    else
        warn "RustDesk not found — will install"
    fi

    if [[ "$RD_INSTALLED" == false || "$RD_NEEDS_UPGRADE" == true ]]; then
        echo "    Downloading RustDesk..."
        RD_DEB="/tmp/rustdesk.deb"
        ARCH=$(dpkg --print-architecture 2>/dev/null || echo "amd64")
        RD_URL="https://github.com/rustdesk/rustdesk/releases/latest/download/rustdesk-1.2.3-${ARCH}.deb"

        if wget -q "$RD_URL" -O "$RD_DEB" 2>/dev/null && [[ -s "$RD_DEB" ]]; then
            case "$PKG_MGR" in
                apt)
                    apt-get install -y -qq "$RD_DEB" || { dpkg -i "$RD_DEB" && apt-get install -f -y -qq; }
                    ;;
                *)
                    warn "Automatic install only supported on Debian/Ubuntu (.deb). Download manually: $RD_URL"
                    ;;
            esac
            rm -f "$RD_DEB"
        else
            warn "Could not download RustDesk from $RD_URL — skipping"
        fi

        if command -v rustdesk &>/dev/null; then ok "RustDesk installed/upgraded"; fi
    fi

    if command -v rustdesk &>/dev/null; then
        # Always re-check config — RustDesk may have been previously installed
        # and pointed at a different relay (public servers, or another deployment).
        RD_CONFIG_PATH="/root/.config/rustdesk/RustDesk2.toml"
        RD_ALREADY_CORRECT=false
        if [[ -f "$RD_CONFIG_PATH" ]] && grep -q "$RD_SERVER_ADDR" "$RD_CONFIG_PATH" 2>/dev/null; then
            RD_ALREADY_CORRECT=true
        fi

        if [[ "$RD_ALREADY_CORRECT" == true ]]; then
            ok "RustDesk already configured for this server ($RD_SERVER_ADDR) — skipping reconfiguration"
        else
            echo "    Applying server + password configuration..."
            rustdesk --config "host=$RD_SERVER_ADDR,key=" 2>/dev/null || true
            rustdesk --password "$RD_PASSWORD" 2>/dev/null || true
            ok "RustDesk reconfigured for this server (was previously pointed elsewhere, or freshly installed)"
        fi

        # Auto-start RustDesk for any desktop user via XDG autostart
        mkdir -p /etc/xdg/autostart
        cat > /etc/xdg/autostart/rustdesk.desktop << AUTOSTART
[Desktop Entry]
Type=Application
Name=RustDesk
Exec=rustdesk --tray
Icon=rustdesk
Terminal=false
X-GNOME-Autostart-enabled=true
AUTOSTART
        ok "RustDesk configured (server: $RD_SERVER_ADDR), autostart enabled for desktop sessions"
        echo "    The agent reports this machine's RustDesk ID to the dashboard on its next heartbeat."
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
echo "  Remote desktop: RustDesk configured (ID visible in dashboard after first heartbeat)"
echo ""
echo "  Manage:"
echo "    Status:  systemctl status $SERVICE_NAME"
echo "    Logs:    journalctl -u $SERVICE_NAME -f"
echo "    Stop:    systemctl stop $SERVICE_NAME"
echo "    Start:   systemctl start $SERVICE_NAME"
echo "    Remove:  systemctl disable --now $SERVICE_NAME && rm /etc/systemd/system/${SERVICE_NAME}.service"
echo ""
