FROM mcr.microsoft.com/dotnet/sdk:10.0.401-resolute-aot@sha256:ae581c66994fd9520cb09d6db419aee49dfa110ff2f81634091ce7e0d39f150a AS build
ARG TARGETARCH
ARG VERSION=0.3.5
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
FROM ghcr.io/robintra/perf-sentinel:0.25.5@sha256:5e0896bb79e20f528dd35d0b86ece229c27b0fc46be0c63b697ba99aa8a6956f AS engine

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.12-resolute-chiseled-extra@sha256:2b0f7a348127985b5accc794e1304b0f3f9ab9c8db652096514db4b831dc3ce4
ARG SOURCE_COMMIT=unknown
LABEL org.opencontainers.image.version="0.3.5" \
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
