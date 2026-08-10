FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS build
ARG TARGETARCH
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY . .
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac \
    && dotnet restore PerfSentinelHub.sln --locked-mode \
    && dotnet publish PerfSentinelHub/PerfSentinelHub.csproj -c Release -r "$rid" \
       --self-contained true -p:PublishAot=true --no-restore -o /out

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.10-noble-chiseled-extra@sha256:fb2d373f44be85cb0d12fec9f4a464ce9cffce2ddf023cd1f5ecc5b146b8186c
WORKDIR /app
COPY --from=build --chown=1654:1654 /out/PerfSentinelHub /app/PerfSentinelHub
COPY --from=build --chown=1654:1654 /out/libe_sqlite3.so /app/libe_sqlite3.so
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["/app/PerfSentinelHub"]
