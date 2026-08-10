.PHONY: restore test publish audit image image-scan helm-lint helm-template verify

restore:
	dotnet restore PerfSentinelHub.sln --locked-mode

test: restore
	dotnet test PerfSentinelHub.sln -c Release --no-restore

publish: restore
	dotnet publish PerfSentinelHub/PerfSentinelHub.csproj -c Release -r linux-$${TARGETARCH:-arm64} --self-contained true -p:PublishAot=true --no-restore

audit: restore
	dotnet package list --project PerfSentinelHub.sln --vulnerable --include-transitive --format json --no-restore > /tmp/perf-sentinel-hub-vulnerabilities.json
	python3 -c 'import json,sys; d=json.load(open("/tmp/perf-sentinel-hub-vulnerabilities.json")); v=[x for p in d.get("projects",[]) for f in p.get("frameworks",[]) for k in ("topLevelPackages","transitivePackages") for x in f.get(k,[]) if x.get("vulnerabilities")]; sys.exit(bool(v))'

image:
	docker build --platform linux/$${TARGETARCH:-arm64} -t perf-sentinel-hub:$${TAG:-local} .

image-scan: image
	trivy image --exit-code 1 --ignore-unfixed --severity HIGH,CRITICAL perf-sentinel-hub:$${TAG:-local}

helm-lint:
	helm lint deploy/helm/perf-sentinel-hub --set 'sources[0].id=test' --set 'sources[0].name=test' --set 'sources[0].environment=test' --set 'sources[0].baseUrl=http://perf-sentinel:4318'

helm-template:
	helm template test deploy/helm/perf-sentinel-hub --set 'sources[0].id=test' --set 'sources[0].name=test' --set 'sources[0].environment=test' --set 'sources[0].baseUrl=http://perf-sentinel:4318' >/dev/null

verify: test publish audit image-scan helm-lint helm-template
