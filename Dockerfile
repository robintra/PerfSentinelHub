FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble-aot@sha256:1a069a730888d278b5ac00e3e238707387d20d2b65f0fc6866b6180df0a767e0 AS build
ARG TARGETARCH
ARG VERSION=0.3.2
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
FROM ghcr.io/robintra/perf-sentinel:0.25.1@sha256:c9786c5c7948464aa736b4d17938c3a6df08e1fed593c71c277aaf59ea98fb04 AS engine

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.12-noble-chiseled-extra@sha256:9a3e4e315a3eae3be20b73739ae84fa7e4ffb2a47a234f3bef30825f0543fe1b
ARG SOURCE_COMMIT=unknown
LABEL org.opencontainers.image.version="0.3.2" \
      org.opencontainers.image.revision="$SOURCE_COMMIT" \
      org.opencontainers.image.source="https://github.com/robintra/PerfSentinelHub"
WORKDIR /app
# Root owns what it runs, so the service account cannot rewrite its own binary.
COPY --from=build /out/PerfSentinelHub /app/PerfSentinelHub
COPY --from=build /out/libe_sqlite3.so /app/libe_sqlite3.so
# The launcher is static files beside the binary, and without them every page answers 404.
COPY --from=build /out/wwwroot /app/wwwroot
COPY --from=engine /perf-sentinel /app/perf-sentinel
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER 1654:1654
ENTRYPOINT ["/app/PerfSentinelHub"]
