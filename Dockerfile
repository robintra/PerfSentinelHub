FROM mcr.microsoft.com/dotnet/sdk:10.0.400-noble-aot@sha256:2ef30f119199e148cb35fd954ca61eddcf02f2996059f782899d318451ff4967 AS build
ARG TARGETARCH
ARG VERSION=0.1.6
ARG SOURCE_DATE_EPOCH
WORKDIR /src
COPY . .
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac \
    && dotnet restore PerfSentinelHub.sln --locked-mode \
    && dotnet publish PerfSentinelHub/PerfSentinelHub.csproj -c Release -r "$rid" \
       --self-contained true -p:PublishAot=true -p:Version="$VERSION" --no-restore -o /out

# The engine the Hub runs for an analysis. Pinned by digest like every other
# image here, and copied rather than downloaded so the build reaches no host
# outside the registry.
FROM ghcr.io/robintra/perf-sentinel:0.20.0@sha256:08b95c5ea1421e1b89ed017f7dfa3f9f0da54f9a21d8f02a6ef117057274e126 AS engine

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.11-noble-chiseled-extra@sha256:4bf18f918ddae188e11fc4a496e36eae78c43c927720b162bcd8a567e9bebc30
ARG SOURCE_COMMIT=unknown
LABEL org.opencontainers.image.version="0.1.6" \
      org.opencontainers.image.revision="$SOURCE_COMMIT" \
      org.opencontainers.image.source="https://github.com/robintra/PerfSentinelHub"
WORKDIR /app
# Root owns what it runs, so the service account cannot rewrite its own binary.
COPY --from=build /out/PerfSentinelHub /app/PerfSentinelHub
COPY --from=build /out/libe_sqlite3.so /app/libe_sqlite3.so
COPY --from=engine /perf-sentinel /app/perf-sentinel
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER 1654:1654
ENTRYPOINT ["/app/PerfSentinelHub"]
