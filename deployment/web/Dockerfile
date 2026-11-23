# flow-data-simulator-web: the interview page with in-container plant hosting.
#
# Build from the repository root (BuildKit picks up Dockerfile.dockerignore):
#   docker build -f deployment/web/Dockerfile -t flow-data-simulator-web:2.0.0-rc.3 .
# Native linux-arm64 and linux-x64 images come from the same file
# (docker buildx build --platform linux/amd64,linux/arm64 ...). The web image is
# Docker-only, so osx-arm64 and win-x64 do not apply to it.
#
# The image holds two self-contained programs that share one .NET runtime:
#   flow-data-simulator-web   the page and the hosted plants (entrypoint)
#   IndustrialDataSim.Cli     the CLI, for `plant gage-check` and diagnostics
# The storyline library, base models and demo guide template are published
# next to the web program (./storylines).

ARG VERSION=2.0.0-rc.3

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION
WORKDIR /src
COPY global.json Directory.Build.props IndustrialDataSim.slnx ./
COPY src/ src/
COPY examples/storylines/ examples/storylines/
COPY tools/plant-preview/ tools/plant-preview/
RUN case "$TARGETARCH" in \
      amd64) rid=linux-x64 ;; \
      arm64) rid=linux-arm64 ;; \
      *) echo "Unsupported architecture $TARGETARCH: build for linux/amd64 or linux/arm64." >&2; exit 1 ;; \
    esac \
    && for project in src/IndustrialDataSim.Web src/IndustrialDataSim.Cli; do \
         dotnet publish "$project" -c Release -r "$rid" --self-contained true -o /out/app \
           -p:Version="$VERSION" -p:DebugType=None -p:DebugSymbols=false \
           -p:NuGetAudit=true -p:NuGetAuditMode=all --disable-build-servers || exit 1; \
       done

# Same base as the CLI image (flow-data-simulator).
FROM debian:trixie-slim
ARG VERSION
LABEL org.opencontainers.image.title="Flow Data Simulator web" \
      org.opencontainers.image.description="Interview page that builds and hosts simulated Timebase demo plants" \
      org.opencontainers.image.version="$VERSION"
RUN apt-get update && apt-get install -y --no-install-recommends libicu76 \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --uid 10001 datasim
COPY --from=build /out/app/ /opt/datasim/app/
USER datasim
WORKDIR /opt/datasim/app
# Listen on every container interface; publish the port on the host's loopback
# only (127.0.0.1:8480:8480), because the page has no login.
ENV FLOWSIM_WEB_URLS=http://0.0.0.0:8480 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
EXPOSE 8480
# The program stops plants cleanly (final snapshot, pending sends) on SIGINT.
# Give it time: docker stop --time 150, or stop_grace_period: 150s in compose.
STOPSIGNAL SIGINT
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD ["/opt/datasim/app/flow-data-simulator-web", "healthcheck"]
ENTRYPOINT ["/opt/datasim/app/flow-data-simulator-web"]
