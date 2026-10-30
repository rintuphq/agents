#!/usr/bin/env bash
# RintUp server monitoring agent: install / upgrade / uninstall (Linux, systemd).
#
#   curl -fsSL https://get.rintup.com/agent | sudo bash -s -- --token srv_xxx
#   sudo bash install.sh --token srv_xxx [--url https://rintup.com] [--processes nginx,postgres]
#   sudo bash install.sh --uninstall
#
# Options:
#   --token TOKEN       Token shown once in the dashboard when adding a server (srv_...). On reinstall, if
#                       omitted, the one in /etc/rintup-agent.conf is kept. To keep it out of shell history
#                       it can also be passed via the RINTUP_TOKEN environment variable.
#   --url URL           RintUp address (default https://rintup.com). https:// only.
#   --processes LIST    Comma-separated processes to monitor (process rules added in the dashboard are monitored too).
#   --version VERSION   Agent version (default latest).
#   --binary FILE       Install a local binary instead of downloading (offline install / your own build). If AGENT_SHA256
#                       is set, the digest is checked against it; otherwise the digest is only printed.
#   --uninstall         Stops the service; removes the binary, configuration, unit and user.
#
# Environment:
#   AGENT_DOWNLOAD_BASE  Base URL for the binary (default https://get.rintup.com/agent). The downloaded
#                        rintup-agent-<linux-x64|linux-arm64> file is VERIFIED against the .sha256 next to it;
#                        on mismatch the install stops.
#   AGENT_SHA256         (optional) Expected SHA-256: pins to a digest obtained through a channel independent of
#                        the download source (release notes). If set, it is checked in addition to the .sha256.
#
# Security: the agent does NOT run as root; it runs as the rintup-agent user with a systemd sandbox (NoNewPrivileges,
# ProtectSystem=strict, ProtectHome, PrivateTmp, ReadOnlyPaths=/, no capabilities). The token lives only in
# /etc/rintup-agent.conf (owner rintup-agent, mode 600) and is never printed to the screen or logs.
set -euo pipefail

AGENT_USER="rintup-agent"
BIN_PATH="/usr/local/bin/rintup-agent"
CONF_PATH="/etc/rintup-agent.conf"
UNIT_PATH="/etc/systemd/system/rintup-agent.service"
DOWNLOAD_BASE=${AGENT_DOWNLOAD_BASE:-https://get.rintup.com/agent}
EXPECTED_SHA256=${AGENT_SHA256:-}
TOKEN=${RINTUP_TOKEN:-}
URL="https://rintup.com"
PROCESSES=""
VERSION="latest"
UNINSTALL=0
BINARY=""

log()  { printf '\033[1;36m==> %s\033[0m\n' "$*"; }
info() { printf '    %s\n' "$*"; }
die()  { printf '\033[1;31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --token) TOKEN=${2:?--token requires a value}; shift 2 ;;
    --url) URL=${2:?--url requires a value}; shift 2 ;;
    --processes) PROCESSES=${2:?--processes requires a value}; shift 2 ;;
    --version) VERSION=${2:?--version requires a value}; shift 2 ;;
    --binary) BINARY=${2:?--binary requires a value}; shift 2 ;;
    --uninstall) UNINSTALL=1; shift ;;
    -h|--help) sed -n '2,28p' "$0"; exit 0 ;;
    *) die "Unknown option: $1 (--help)" ;;
  esac
done

[ "$(id -u)" -eq 0 ] || die "Run as root (sudo)."
command -v systemctl >/dev/null 2>&1 || die "systemd is required (systemctl not found)."

uninstall() {
  log "Uninstalling rintup-agent"
  systemctl disable --now rintup-agent.service >/dev/null 2>&1 || true
  rm -f "$UNIT_PATH" "$BIN_PATH" "$CONF_PATH"
  systemctl daemon-reload
  if id "$AGENT_USER" >/dev/null 2>&1; then
    userdel "$AGENT_USER" >/dev/null 2>&1 || info "Could not delete user: $AGENT_USER"
  fi
  info "Uninstalled. You can also delete the server record in the dashboard."
}

if [ "$UNINSTALL" -eq 1 ]; then
  uninstall
  exit 0
fi

# --- Input validation ---
URL=${URL%/}
[[ "$URL" =~ ^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?(/[A-Za-z0-9._~/-]*)?$ ]] || die "Invalid --url: must be of the form https://domain.name[:port]."
case "$DOWNLOAD_BASE" in
  https://*) DOWNLOAD_BASE=${DOWNLOAD_BASE%/} ;;
  *) die "AGENT_DOWNLOAD_BASE must start with https://." ;;
esac
[[ "$VERSION" =~ ^(latest|[0-9]+\.[0-9]+\.[0-9]+)$ ]] || die "--version must be 'latest' or X.Y.Z."
[[ -z "$PROCESSES" || "$PROCESSES" =~ ^[A-Za-z0-9._+:@,-]+$ ]] || die "--processes: comma-separated process names (letters, digits, . _ - + : @)."
if [ -n "$TOKEN" ]; then
  [[ "$TOKEN" =~ ^srv_[0-9A-Za-z]{43}$ ]] || die "Invalid token format (47 characters starting with srv_)."
elif [ ! -f "$CONF_PATH" ]; then
  die "--token is required (dashboard: Servers > Add)."
fi

case "$(uname -m)" in
  x86_64|amd64) RID="linux-x64" ;;
  aarch64|arm64) RID="linux-arm64" ;;
  *) die "Unsupported architecture: $(uname -m) (x86_64 and arm64 are supported)." ;;
esac

command -v curl >/dev/null 2>&1 || die "curl is required."
command -v sha256sum >/dev/null 2>&1 || die "sha256sum is required (coreutils)."

# --- Download and verify ---
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
ASSET="rintup-agent-$RID"
if [ -n "$BINARY" ]; then
  [ -f "$BINARY" ] || die "Binary not found: $BINARY"
  log "Local binary: $BINARY"
  cp -f "$BINARY" "$WORK/$ASSET"
  ACTUAL=$(sha256sum "$WORK/$ASSET" | awk '{ print tolower($1) }')
else
  log "Downloading: $VERSION/$ASSET"
  fetch() { curl --proto '=https' --tlsv1.2 -fsSL --retry 3 --max-time 120 -o "$2" "$1"; }
  fetch "$DOWNLOAD_BASE/$VERSION/$ASSET" "$WORK/$ASSET" || die "Could not download the binary."
  fetch "$DOWNLOAD_BASE/$VERSION/$ASSET.sha256" "$WORK/$ASSET.sha256" || die "Could not download the SHA-256 file; refusing to install unverified."

  PUBLISHED=$(awk 'NR == 1 { print tolower($1) }' "$WORK/$ASSET.sha256")
  ACTUAL=$(sha256sum "$WORK/$ASSET" | awk '{ print tolower($1) }')
  [[ "$PUBLISHED" =~ ^[0-9a-f]{64}$ ]] || die "SHA-256 file is malformed."
  [ "$ACTUAL" = "$PUBLISHED" ] || die "SHA-256 mismatch: the downloaded binary is modified or corrupt. Installation aborted."
fi
if [ -n "$EXPECTED_SHA256" ]; then
  [ "$ACTUAL" = "$(printf '%s' "$EXPECTED_SHA256" | tr 'A-F' 'a-f')" ] || die "SHA-256 does not match AGENT_SHA256. Installation aborted."
fi
if [ -n "$BINARY" ] && [ -z "$EXPECTED_SHA256" ]; then
  info "SHA-256 (local binary, not verified): $ACTUAL"
else
  info "SHA-256 verified: $ACTUAL"
fi

# --- User, binary, configuration ---
if ! id "$AGENT_USER" >/dev/null 2>&1; then
  log "Creating user: $AGENT_USER (no login shell)"
  useradd --system --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin "$AGENT_USER"
fi

log "Installing: $BIN_PATH"
install -o root -g root -m 0755 "$WORK/$ASSET" "$BIN_PATH"
"$BIN_PATH" --version >/dev/null || die "The binary does not run (architecture/library mismatch)."

if [ -n "$TOKEN" ] || [ ! -f "$CONF_PATH" ]; then
  log "Writing configuration: $CONF_PATH (mode 600)"
  (
    umask 077
    {
      echo "# RintUp agent. The token is secret: this file must stay mode 600."
      echo "RINTUP_URL=$URL"
      echo "RINTUP_TOKEN=$TOKEN"
      echo "RINTUP_PROCESSES=$PROCESSES"
    } >"$CONF_PATH.tmp"
  )
  mv -f "$CONF_PATH.tmp" "$CONF_PATH"
else
  info "Keeping existing configuration: $CONF_PATH"
fi
chown "$AGENT_USER:$AGENT_USER" "$CONF_PATH"
chmod 0600 "$CONF_PATH"

# --- systemd unit ---
log "Writing systemd unit: $UNIT_PATH"
cat >"$UNIT_PATH" <<'UNIT'
[Unit]
Description=RintUp server monitoring agent
Documentation=https://rintup.com/docs/agent
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=rintup-agent
Group=rintup-agent
ExecStart=/usr/local/bin/rintup-agent --config /etc/rintup-agent.conf
Restart=always
RestartSec=10
# Sandbox: the agent only reads /proc and /etc/rintup-agent.conf, and connects to RintUp over HTTPS.
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=read-only
ReadOnlyPaths=/
PrivateTmp=yes
PrivateDevices=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectKernelLogs=yes
ProtectControlGroups=yes
ProtectClock=yes
ProtectHostname=yes
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX
RestrictNamespaces=yes
RestrictRealtime=yes
RestrictSUIDSGID=yes
LockPersonality=yes
SystemCallArchitectures=native
CapabilityBoundingSet=
AmbientCapabilities=
UMask=0077
MemoryMax=128M
CPUQuota=20%

[Install]
WantedBy=multi-user.target
UNIT
chmod 0644 "$UNIT_PATH"

systemctl daemon-reload
systemctl enable rintup-agent.service >/dev/null
systemctl restart rintup-agent.service

log "rintup-agent is running"
info "Status: systemctl status rintup-agent"
info "Logs:   journalctl -u rintup-agent -f"
info "The server will show as 'Running' in the dashboard within a few minutes."
