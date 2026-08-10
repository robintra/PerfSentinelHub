.PHONY: restore test publish image image-scan helm-lint verify

restore:
	dotnet restore PerfSentinelHub.sln --locked-mode

test: restore
	dotnet test PerfSentinelHub.sln -c Release --no-restore

publish: restore
	dotnet publish PerfSentinelHub/PerfSentinelHub.csproj -c Release -r linux-$${TARGETARCH:-arm64} --self-contained true -p:PublishAot=true --no-restore

image:
	docker build --platform linux/$${TARGETARCH:-arm64} -t perf-sentinel-hub:$${TAG:-local} .

image-scan: image
	trivy image --exit-code 1 --ignore-unfixed --severity HIGH,CRITICAL perf-sentinel-hub:$${TAG:-local}

helm-lint:
	helm lint deploy/helm/perf-sentinel-hub --set 'sources[0].id=test' --set 'sources[0].name=test' --set 'sources[0].environment=test' --set 'sources[0].baseUrl=http://perf-sentinel:4318'

verify: test publish image-scan helm-lint
