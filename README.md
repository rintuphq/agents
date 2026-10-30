# RintUp agents

Source code of the [RintUp](https://rintup.com) monitoring agent. One small Native AOT binary (`rintup-agent`,
.NET, no package dependencies) runs in three roles:

| Role | How it runs | What it does |
| -- | -- | -- |
| Server agent | systemd service on a Linux server (`install.sh`) | Sends host metrics to RintUp; the server appears under **Servers**. |
| Kubernetes node agent | `rintup-agent` in a DaemonSet | The same host metrics for every node, read from the host's `/proc` mounted read-only. |
| Kubernetes cluster agent | `rintup-agent cluster` in a Deployment | Watches the Kubernetes API read-only and reports health findings that open and resolve incidents. |

For Kubernetes, install the Helm chart from [rintuphq/helm-charts](https://github.com/rintuphq/helm-charts)
(`oci://ghcr.io/rintuphq/charts/rintup-agent`); it runs the container image `ghcr.io/rintuphq/agent` built here.

## What the server agent measures

Every interval (default 60 s, `RINTUP_INTERVAL_SECONDS` 10-3600) the agent reads `/proc` and `statvfs` and sends:

- CPU usage (%) and load average (1, 5, 15 min)
- memory total / used / available, swap total / used
- disk total and used bytes per mounted file system (up to 64 mount points)
- network receive and transmit rate (bytes per second)
- uptime and process count, plus the hostname and the OS name (from `/etc/os-release`)
- whether watched processes are running: names from `RINTUP_PROCESSES` (or `--processes`) plus the process rules
  you add in RintUp, which the server returns to the agent (at most 50 in total)

CPU and network rates are the difference between two readings, so the first sample has no value for them. If RintUp
cannot be reached, up to 60 samples are kept in memory (the oldest is dropped first) and sent together once the
connection is back. The agent does not run commands, does not read files other than these system files and its own
configuration, and does not collect logs.

## Network and security

- **Outbound HTTPS only.** The agent sends `POST {RINTUP_URL}/api/v1/agent/metrics` to `https://rintup.com`
  (port 443) with `Authorization: Bearer srv_...`. It opens no listening port. TLS 1.2 or 1.3 is required,
  certificate validation cannot be turned off, and redirects are not followed (the token is never sent elsewhere).
  An egress proxy is honoured through the standard `HTTPS_PROXY` / `NO_PROXY` environment variables.
- **No root.** `install.sh` creates a system user `rintup-agent` without a login shell; the service runs as that user.
- **systemd sandbox.** `NoNewPrivileges`, `ProtectSystem=strict`, `ProtectHome=read-only`, `ReadOnlyPaths=/`,
  `PrivateTmp`, `PrivateDevices`, kernel tunables/modules/logs/control groups/clock/hostname protected,
  `RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX`, `RestrictNamespaces`, `RestrictSUIDSGID`,
  `LockPersonality`, `SystemCallArchitectures=native`, empty capability sets, `UMask=0077`, `MemoryMax=128M`,
  `CPUQuota=20%`.
- **Token.** Stored only in `/etc/rintup-agent.conf` (owner `rintup-agent`, mode 600). It is never printed or
  logged; log lines carry only status codes or exception types, never headers or bodies. The agent warns if the
  configuration file is readable by group or others.

## Install (server agent)

In RintUp, **Servers > Add** shows a one-time `srv_` token and the command:

```bash
curl -fsSL https://get.rintup.com/agent | sudo bash -s -- --token srv_...
```

Supported: Linux with systemd on x86_64 or arm64.

| Option | Description |
| -- | -- |
| `--token TOKEN` | Server token (`srv_...`). On reinstall it can be omitted; the existing configuration is kept. Can also be passed as `RINTUP_TOKEN` to keep it out of shell history. |
| `--url URL` | RintUp address (default `https://rintup.com`, https only). |
| `--processes LIST` | Comma-separated process names to watch, e.g. `nginx,postgres`. |
| `--version X.Y.Z` | Agent version (default `latest`). |
| `--binary FILE` | Install a local binary instead of downloading it (offline install or your own build). |
| `--uninstall` | Stop the service and remove the binary, configuration, unit and user. |

Running the script again upgrades the agent in place. Status: `systemctl status rintup-agent`, logs:
`journalctl -u rintup-agent -f`.

## Verifying downloads

`install.sh` downloads `rintup-agent-linux-x64` or `rintup-agent-linux-arm64` together with its `.sha256` file and
refuses to install when the checksum does not match. To pin a checksum obtained independently (for example from
the release notes), set `AGENT_SHA256=<sha256>`; both checks must pass.

Each [GitHub release](https://github.com/rintuphq/agents/releases) contains the binaries, their `.sha256` files,
`install.sh` and a [Sigstore](https://www.sigstore.dev) bundle (`.sigstore.json`) for each file, created by cosign
keyless signing in this repository's release workflow. To verify by hand:

```bash
sha256sum -c rintup-agent-linux-x64.sha256
cosign verify-blob rintup-agent-linux-x64 \
  --bundle rintup-agent-linux-x64.sigstore.json \
  --certificate-identity-regexp '^https://github.com/rintuphq/agents/.github/workflows/release.yml@refs/tags/v' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
sudo bash install.sh --binary ./rintup-agent-linux-x64 --token srv_...
```

The container image is signed the same way:

```bash
cosign verify ghcr.io/rintuphq/agent@sha256:... \
  --certificate-identity-regexp '^https://github.com/rintuphq/agents/.github/workflows/release.yml@refs/tags/v' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

## Configuration

`/etc/rintup-agent.conf` holds `KEY=value` lines; environment variables override it.

| Variable | Default | Description |
| -- | -- | -- |
| `RINTUP_TOKEN` | none (required) | `srv_...` for a server, `rka_...` (cluster key) on Kubernetes. |
| `RINTUP_URL` | `https://rintup.com` | RintUp address, https only. |
| `RINTUP_PROCESSES` | empty | Comma-separated process names (letters, digits, `. _ - + : @`), at most 50. |
| `RINTUP_INTERVAL_SECONDS` | `60` | 10-3600. |
| `RINTUP_ALLOW_INSECURE_HTTP` | `false` | Allows `http://` for local development only. |
| `NODE_NAME`, `HOST_PROC`, `HOST_ROOT` | | Kubernetes node agent: node name (downward API) and where the host `/proc` and `/` are mounted. Set by the Helm chart. |

Usage: `rintup-agent [--config <path>]`, `rintup-agent cluster`, `rintup-agent --version`.

## Build from source

Requirements: the .NET SDK version in `global.json`.

```bash
dotnet build RintUp.Agent.slnx -c Release
dotnet test --project tests/RintUp.Agent.Tests -c Release
```

Native AOT binary (on Linux; needs `clang` and the zlib development package):

```bash
dotnet publish src/RintUp.Agent/RintUp.Agent.csproj -c Release -r linux-x64 -o out   # or linux-arm64
./out/rintup-agent --version
```

Container image (single architecture, built from the repository root):

```bash
docker build -t rintup-agent:dev .
```

Warnings are errors (`Directory.Build.props`), package versions are central (`Directory.Packages.props`) and locked
(`packages.lock.json`). The agent itself uses only the .NET base class library; xUnit is used by the tests.

## Workflows

- `.github/workflows/ci.yml`: build, tests, a Native AOT publish, `shellcheck install.sh` and a container build on
  pushes to main and pull requests.
- `.github/workflows/release.yml`: on a `vX.Y.Z` tag (which must match `AgentVersion.Current` in
  `src/RintUp.Agent/AgentRunner.cs`) builds the linux-x64 and linux-arm64 Native AOT binaries natively on
  `ubuntu-24.04` and `ubuntu-24.04-arm` runners (inside an AlmaLinux 8 container, so they need glibc 2.28 or newer),
  writes `.sha256` files, signs every file with cosign keyless (`sign-blob`, `.sigstore.json` bundles), builds the
  multi-arch image `ghcr.io/rintuphq/agent:X.Y.Z` with SBOM and provenance, scans it with Trivy, signs it with
  cosign and publishes a GitHub release. It needs only the default `GITHUB_TOKEN`; make the `agent` package public
  in the organization's package settings after the first release.

## Security

See [SECURITY.md](SECURITY.md). Report vulnerabilities privately to security@rintup.com.

## License

[Apache-2.0](LICENSE)
