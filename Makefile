NATIVE_RIDS := linux-x64 linux-arm64 osx-arm64 win-x64
OUTPUT ?= dist

.PHONY: restore test publish package-native audit image image-scan helm-lint helm-template verify

restore:
	dotnet restore PerfSentinelHub.sln --locked-mode

test: restore
	dotnet test PerfSentinelHub.sln -c Release --no-restore

publish: restore
	dotnet publish PerfSentinelHub/PerfSentinelHub.csproj -c Release -r linux-$${TARGETARCH:-arm64} --self-contained true -p:PublishAot=true --no-restore

package-native:
	@case " $(NATIVE_RIDS) " in *" $(RID) "*) ;; *) echo "RID must be one of $(NATIVE_RIDS)" >&2; exit 2;; esac
	@test -n "$(VERSION)" || { echo "VERSION is required" >&2; exit 2; }
	@test -n "$(COMMIT_TIME)" || { echo "COMMIT_TIME is required" >&2; exit 2; }
	dotnet restore PerfSentinelHub/PerfSentinelHub.csproj -r "$(RID)" --locked-mode
	dotnet publish PerfSentinelHub/PerfSentinelHub.csproj -c Release -r "$(RID)" --self-contained true -p:PublishAot=true -p:Version="$(VERSION)" --no-restore
	python3 scripts/package-native.py --rid "$(RID)" --version "$(VERSION)" --commit-time "$(COMMIT_TIME)" --input "PerfSentinelHub/bin/Release/net10.0/$(RID)/publish" --output "$(OUTPUT)"

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
