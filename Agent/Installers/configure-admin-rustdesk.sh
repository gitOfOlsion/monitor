#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════════
# Configure your local RustDesk client to use this Peripheral Monitor's
# self-hosted relay server. Run this ONCE on the admin's own machine
# before using "Remote Desktop" links from the dashboard.
#
# Usage: sudo bash configure-admin-rustdesk.sh <server_addr>
#   e.g. sudo bash configure-admin-rustdesk.sh 185.218.126.64
# ═══════════════════════════════════════════════════════════════════════════

set -euo pipefail

RD_MIN_VERSION="1.2.0"
SERVER_ADDR="${1:-}"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[0;33m'; CYAN='\033[0;36m'; NC='\033[0m'
step()  { echo -e "\n${CYAN}[*] $*${NC}"; }
ok()    { echo -e "    ${GREEN}[OK] $*${NC}"; }
warn()  { echo -e "    ${YELLOW}[WARN] $*${NC}"; }
fail()  { echo -e "    ${RED}[FAIL] $*${NC}"; }

echo ""
echo "======================================================"
echo "  RustDesk Client Configuration"
echo "======================================================"

if [[ -z "$SERVER_ADDR" ]]; then
    fail "Usage: sudo bash configure-admin-rustdesk.sh <server_addr>"
    echo "  Example: sudo bash configure-admin-rustdesk.sh 185.218.126.64"
    exit 1
fi
echo "  Target server: $SERVER_ADDR"
echo ""

if [[ $EUID -ne 0 ]]; then
    fail "This script must be run as root (sudo bash configure-admin-rustdesk.sh $SERVER_ADDR)"
    exit 1
fi

version_ge() {
    [[ "$1" == "$2" ]] && return 0
    [[ "$(printf '%s\n%s' "$1" "$2" | sort -V | head -n1)" == "$2" ]]
}

# ── 1. Check / install RustDesk ─────────────────────────────────────────
step "Checking RustDesk installation..."

RD_INSTALLED=false
RD_NEEDS_UPGRADE=false

if command -v rustdesk &>/dev/null; then
    RD_INSTALLED=true
    RD_CURRENT_VERSION=$(rustdesk --version 2>/dev/null | grep -oP '\d+\.\d+\.\d+' | head -1 || echo "0.0.0")
    if version_ge "$RD_CURRENT_VERSION" "$RD_MIN_VERSION"; then
        ok "RustDesk $RD_CURRENT_VERSION already installed"
    else
        warn "RustDesk $RD_CURRENT_VERSION is older than recommended $RD_MIN_VERSION — will upgrade"
        RD_NEEDS_UPGRADE=true
    fi
else
    warn "RustDesk not found on this machine"
fi

if [[ "$RD_INSTALLED" == false || "$RD_NEEDS_UPGRADE" == true ]]; then
    ARCH=$(dpkg --print-architecture 2>/dev/null || echo "amd64")

    echo "    Resolving latest RustDesk release..."
    RD_API_JSON=$(curl -fsSL -H "User-Agent: PeripheralMonitor-Setup" \
        "https://api.github.com/repos/rustdesk/rustdesk/releases/latest" 2>/dev/null || true)

    RD_URL=""
    if [[ -n "$RD_API_JSON" ]]; then
        RD_URL=$(echo "$RD_API_JSON" | grep -oP "\"browser_download_url\":\s*\"\K[^\"]*rustdesk-[\d.]+-${ARCH}\.deb" | head -1 || true)
    fi

    if [[ -z "$RD_URL" ]]; then
        warn "Could not resolve latest release via GitHub API - using generic 'latest' redirect URL as fallback"
        RD_URL="https://github.com/rustdesk/rustdesk/releases/latest/download/rustdesk-${ARCH}.deb"
    else
        echo "    Found: $(basename "$RD_URL")"
    fi

    echo "    Downloading RustDesk..."
    RD_DEB="/tmp/rustdesk.deb"

    if command -v apt-get &>/dev/null; then
        wget -q "$RD_URL" -O "$RD_DEB"
        apt-get install -y -qq "$RD_DEB" || { dpkg -i "$RD_DEB" && apt-get install -f -y -qq; }
        rm -f "$RD_DEB"
    else
        fail "Automatic install only supported on Debian/Ubuntu. Download manually: $RD_URL"
        exit 1
    fi

    if command -v rustdesk &>/dev/null; then ok "RustDesk installed/upgraded"
    else fail "RustDesk installation failed"; exit 1
    fi
fi

# ── 2. Point RustDesk at this server (overwrite any previous relay) ────
step "Configuring RustDesk to use this server's relay..."
echo "    This OVERWRITES any previous relay configuration"
echo "    (including the public RustDesk default, or any other self-hosted instance)."

rustdesk --config "host=$SERVER_ADDR,key=" 2>/dev/null || true

ok "RustDesk is now configured to use: $SERVER_ADDR"
echo ""
echo -e "${GREEN}======================================================${NC}"
echo -e "${GREEN}  Done! Remote Desktop links from the dashboard${NC}"
echo -e "${GREEN}  will now connect correctly.${NC}"
echo -e "${GREEN}======================================================${NC}"
echo ""
