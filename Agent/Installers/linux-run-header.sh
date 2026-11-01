#!/bin/sh
# ═══════════════════════════════════════════════════════════════════════════
# Peripheral Monitor Agent — one-file Linux installer (.run)
# ═══════════════════════════════════════════════════════════════════════════
# This short script is followed by a gzip'd tar archive holding the agent binary,
# install.sh and this institution's appsettings.local.json. The server builds the
# file at download time (AgentPackageService.WriteLinuxInstallerAsync).
#
#   sudo sh PeripheralMonitorAgent-Setup-<institution>.run              install / upgrade
#   sh      PeripheralMonitorAgent-Setup-<institution>.run --dry-run    check, change nothing
#   sudo sh PeripheralMonitorAgent-Setup-<institution>.run --uninstall [--purge]
#
# Any option is passed straight to install.sh. Must be run from a file on disk
# (not "curl ... | sh"), because it reads its own payload.
# ═══════════════════════════════════════════════════════════════════════════
set -eu

SELF="$0"
if [ ! -f "$SELF" ]; then
    echo "Save the installer to a file first, then run: sudo sh <file>.run" >&2
    exit 1
fi

if ! command -v bash >/dev/null 2>&1 || ! command -v tar >/dev/null 2>&1; then
    echo "This installer needs bash and tar." >&2
    exit 1
fi

# Root is needed for everything except --dry-run; re-run through sudo if possible.
NEEDS_ROOT=true
for a in "$@"; do
    case "$a" in --dry-run) NEEDS_ROOT=false ;; esac
done
if [ "$NEEDS_ROOT" = true ] && [ "$(id -u)" -ne 0 ]; then
    if command -v sudo >/dev/null 2>&1; then
        exec sudo sh "$SELF" "$@"
    fi
    echo "Run as root: sudo sh $SELF" >&2
    exit 1
fi

TMP="$(mktemp -d /tmp/pm-agent-setup.XXXXXX)"
trap 'rm -rf "$TMP"' EXIT INT TERM

# The archive starts on the line after the marker below.
LINE="$(awk '/^__PM_PAYLOAD_BELOW__$/ { print NR + 1; exit }' "$SELF")"
if [ -z "$LINE" ]; then
    echo "The installer is damaged (payload not found). Download it again." >&2
    exit 1
fi
if ! tail -n +"$LINE" "$SELF" | tar -xzf - -C "$TMP"; then
    echo "The installer is damaged (could not unpack). Download it again." >&2
    exit 1
fi

bash "$TMP/install.sh" "$@"
exit $?
__PM_PAYLOAD_BELOW__
