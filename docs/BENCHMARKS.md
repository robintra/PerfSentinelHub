# Measurements

What the published image weighs, how fast it answers after a cold start, and
how much memory it holds at rest. Every figure below comes from the released
`ghcr.io/robintra/perf-sentinel-hub:0.3.3` image, not from a local build, and
the commands to reproduce them close the page.

The Hub is published with NativeAOT: C# 14 on .NET 10 compiled ahead of time
into one native executable, with no .NET runtime and no JIT in the image. The
base is `runtime-deps` chiseled, which ships the system libraries and nothing
else, and the process runs as the non-root user `1654`.

## Summary

| Figure                       | Value                                                                   |
|------------------------------|-------------------------------------------------------------------------|
| Cold start to first response | 130 ms median, 120 to 138 ms over 10 runs                               |
| Memory at rest               | 20.3 to 20.8 MiB (21.3 to 21.8 MB), 60 s after start, over 3 containers |
| Compressed image, amd64      | 39.8 MB: base 21.4, Hub 11.1, Perf Sentinel 7.4                         |
| Compressed image, arm64      | 37.7 MB: base 20.3, Hub 10.6, Perf Sentinel 6.8                         |

## What the image carries

The image embeds the Perf Sentinel engine binary, which the launcher runs as a
subprocess. The split below separates what belongs to the Hub from what
belongs to the engine and to the base.

| Layer                 | amd64, compressed | arm64, compressed | arm64, on disk |
|-----------------------|-------------------|-------------------|----------------|
| Chiseled base         | 21.4 MB           | 20.3 MB           |                |
| Hub executable        | 9.6 MB            | 9.1 MB            | 21.1 MB        |
| SQLite native library | 0.7 MB            | 0.8 MB            | 1.5 MB         |
| Browser interface     | 0.8 MB            | 0.8 MB            |                |
| Perf Sentinel 0.25.2  | 7.4 MB            | 6.8 MB            | 15.2 MB        |

On amd64 the executables take 20.4 MB for the Hub and 19.1 MB for the engine
once unpacked.

## Conditions

- Machine: Apple M4 Pro, Docker 29.8.0, the arm64 variant of the image.
- One configured daemon source pointing at a closed port, so every poll fails
  fast and nothing is ever imported. The database stays empty.
- `/data` is a tmpfs owned by UID `1654`.
- Cold start is timed from `docker run` to the first `200` on `/`, so it
  includes the container start as well as the Hub's own boot.
- Memory is the container figure reported by `docker stats`, which leaves out
  the page cache.
- The image sizes are the layer sizes read from the registry manifest for each
  architecture.

## What these figures do not say

The memory figure is a floor. A Hub that imports findings, keeps observation
days, serves the launcher and renders reports holds more, and this page carries
no figure for that until one is measured on a real fleet. The start and memory
figures are arm64 only. The amd64 image was sized, not run.

## Reproduce

```bash
IMG=ghcr.io/robintra/perf-sentinel-hub:0.3.3
docker pull "$IMG"

docker run -d --name hub -p 18080:8080 \
  --tmpfs /data:uid=1654,gid=1654 \
  -e Hub__Sources__0__Id=bench \
  -e Hub__Sources__0__Name=Bench \
  -e Hub__Sources__0__Environment=test \
  -e Hub__Sources__0__BaseUrl=http://127.0.0.1:9 \
  -e Hub__Sources__0__ImportApiKey="$(openssl rand -hex 16)" \
  "$IMG"

until [ "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:18080/)" = 200 ]; do sleep 0.005; done

sleep 60
docker stats --no-stream --format '{{.MemUsage}}' hub
docker rm -f hub
```

Time the `docker run` and the wait loop together to get the cold start. The
layer sizes come from the manifest of each platform entry in the image index.
