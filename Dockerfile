FROM mcr.microsoft.com/dotnet/sdk:10.0.400-noble-aot@sha256:74edbceabfc6d1a7e4a5764be034fd744a07f60d26703232e74f6a5edc04e8ba AS build
ARG TARGETARCH
ARG VERSION=0.1.0
ARG SOURCE_DATE_EPOCH
WORKDIR /src
COPY . .
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac \
    && dotnet restore PerfSentinelHub.sln --locked-mode \
    && dotnet publish PerfSentinelHub/PerfSentinelHub.csproj -c Release -r "$rid" \
       --self-contained true -p:PublishAot=true -p:Version="$VERSION" --no-restore -o /out

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.11-noble-chiseled-extra@sha256:4bf18f918ddae188e11fc4a496e36eae78c43c927720b162bcd8a567e9bebc30
ARG SOURCE_COMMIT=unknown
LABEL org.opencontainers.image.version="0.1.0" \
      org.opencontainers.image.revision="$SOURCE_COMMIT" \
      org.opencontainers.image.source="https://github.com/robintra/PerfSentinelHub"
WORKDIR /app
# Root owns what it runs, so the service account cannot rewrite its own binary.
COPY --from=build /out/PerfSentinelHub /app/PerfSentinelHub
COPY --from=build /out/libe_sqlite3.so /app/libe_sqlite3.so
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER 1654:1654
ENTRYPOINT ["/app/PerfSentinelHub"]
