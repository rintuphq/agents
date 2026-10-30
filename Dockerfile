# syntax=docker/dockerfile:1
# RintUp agent image (Helm chart: https://github.com/rintuphq/helm-charts, charts/rintup-agent). The same binary runs in two modes:
#   rintup-agent           node agent (DaemonSet; host /proc and / mounted read-only)
#   rintup-agent cluster   cluster agent (Deployment; read-only Kubernetes API access via a service account)
#
# Native AOT single file; the runtime image is Microsoft "chiseled" runtime-deps (distroless: no shell, no package manager;
# only glibc, libssl and CA certificates). The user is not root (65532). The root filesystem runs read-only.
#
# Build context: repo root. The multi-arch image is built NATIVELY (so AOT does not need a cross-compilation toolchain):
# CI builds each architecture on its own runner (amd64: ubuntu-24.04, arm64: ubuntu-24.04-arm), assembles a single
# manifest from the digests, then signs it with cosign (.github/workflows/release.yml, published as ghcr.io/rintuphq/agent).
# Local single-arch build:
#   docker build -t rintup-agent:dev .
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-noble-aot AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/RintUp.Agent/RintUp.Agent.csproj src/RintUp.Agent/packages.lock.json src/RintUp.Agent/
RUN case "${TARGETARCH:-amd64}" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) echo "unsupported architecture: $TARGETARCH" >&2; exit 1 ;; esac \
    && echo "$rid" > /rid \
    && dotnet restore src/RintUp.Agent/RintUp.Agent.csproj -r "$rid"
COPY src/RintUp.Agent/ src/RintUp.Agent/
ARG VERSION=0.0.0
# --no-restore is NOT used: even after the earlier restore with -r, publish skipped the native compilation (ILCompiler)
# and only IL was left in /out (rintup-agent: not found). The restore is repeated here; the NuGet cache comes from the layer.
RUN dotnet publish src/RintUp.Agent/RintUp.Agent.csproj -c Release -r "$(cat /rid)" -o /out -p:Version=$VERSION \
    && rm -f /out/*.dbg \
    && /out/rintup-agent --version

FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION}-noble-chiseled
LABEL org.opencontainers.image.title="rintup-agent" \
      org.opencontainers.image.description="RintUp server and Kubernetes agent" \
      org.opencontainers.image.source="https://github.com/rintuphq/agents" \
      org.opencontainers.image.licenses="Apache-2.0"
# Keep the diagnostics IPC socket from trying to write to /tmp (read-only root filesystem).
ENV DOTNET_EnableDiagnostics=0
COPY --from=build --chmod=0555 /out/rintup-agent /usr/local/bin/rintup-agent
USER 65532:65532
ENTRYPOINT ["/usr/local/bin/rintup-agent"]
