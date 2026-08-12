NATIVE_RIDS := linux-x64 linux-arm64 osx-arm64 win-x64
OUTPUT ?= dist
override COVERAGE_DIR := artifacts/coverage
override COVERAGE_REPORT := $(COVERAGE_DIR)/coverage.cobertura.xml
override SONAR_DIR := artifacts/sonar
override QODANA_RESULTS := artifacts/qodana
override QODANA_IMAGE := jetbrains/qodana-dotnet:2026.1@sha256:c893fb5f5dbe54cd4b9c2cb1bd11d711242add66c5a3ac65fe7fc302cdb8c0a3
QODANA_TIMEOUT_SECONDS ?= 1800

.PHONY: tool-restore restore format build coverage coverage-check analysis-config-check security-exceptions security sonar-prepare qodana python-tests test publish package-native audit image image-scan helm-lint helm-template release-check verify-fast verify

tool-restore:
	dotnet tool restore

restore:
	dotnet restore PerfSentinelHub.sln --locked-mode

format: restore
	dotnet format PerfSentinelHub.sln --verify-no-changes --no-restore

build: restore
	dotnet build PerfSentinelHub.sln -c Release --no-restore --warnaserror

coverage: build
	rm -rf "$(COVERAGE_DIR)"
	mkdir -p "$(COVERAGE_DIR)"
	dotnet test PerfSentinelHub.sln -c Release --no-build --no-restore --settings PerfSentinelHub.Tests/coverage.runsettings --collect:"XPlat Code Coverage" --logger:"trx;LogFileName=tests.trx" --results-directory "$(COVERAGE_DIR)"
	@set -- "$(COVERAGE_DIR)"/*/coverage.cobertura.xml; test "$$#" -eq 1 && test -f "$$1" || { echo "expected exactly one Cobertura report" >&2; exit 1; }; mv "$$1" "$(COVERAGE_REPORT)"

coverage-check: coverage
	python3 scripts/check-coverage.py --current-report "$(COVERAGE_REPORT)"

analysis-config-check:
	python3 scripts/check-analysis-config.py

security-exceptions:
	python3 scripts/check-security-exceptions.py

security: security-exceptions analysis-config-check audit
	python3 scripts/check-supply-chain.py

sonar-prepare: analysis-config-check tool-restore coverage
	rm -rf "$(SONAR_DIR)"
	dotnet tool run reportgenerator -- -reports:"$(COVERAGE_REPORT)" -targetdir:"$(SONAR_DIR)" -reporttypes:SonarQube
	python3 scripts/check-analysis-config.py --require-analysis-inputs

qodana: analysis-config-check
	rm -rf "$(QODANA_RESULTS)"
	mkdir -p "$(QODANA_RESULTS)"
	@set -eu; \
		docker run --rm --name perf-sentinel-hub-qodana \
			-v "$(CURDIR):/data/project" \
			-v "$(CURDIR)/$(QODANA_RESULTS):/data/results" \
			$(if $(QODANA_TOKEN),--env QODANA_TOKEN,) \
			"$(QODANA_IMAGE)" --no-statistics=true & \
		qodana_pid=$$!; \
		( sleep "$(QODANA_TIMEOUT_SECONDS)"; docker stop --time 10 perf-sentinel-hub-qodana >/dev/null 2>&1 || true ) & \
		watchdog_pid=$$!; \
		set +e; wait "$$qodana_pid"; status=$$?; set -e; \
		kill "$$watchdog_pid" >/dev/null 2>&1 || true; \
		wait "$$watchdog_pid" 2>/dev/null || true; \
		exit "$$status"

python-tests:
	python3 -m unittest discover -s scripts/tests

test: coverage

publish: restore
	dotnet publish PerfSentinelHub/PerfSentinelHub.csproj -c Release -r linux-$${TARGETARCH:-arm64} --self-contained true -p:PublishAot=true --no-restore

package-native:
	@case " $(NATIVE_RIDS) " in *" $(RID) "*) ;; *) echo "RID must be one of $(NATIVE_RIDS)" >&2; exit 2;; esac
	@test -n "$(VERSION)" || { echo "VERSION is required" >&2; exit 2; }
	@test -n "$(COMMIT_TIME)" || { echo "COMMIT_TIME is required" >&2; exit 2; }
	dotnet restore PerfSentinelHub/PerfSentinelHub.csproj --locked-mode
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

release-check:
	@test -n "$(VERSION)" || { echo "VERSION is required" >&2; exit 2; }
	python3 scripts/check-version.py "v$(VERSION)"

verify-fast: tool-restore format python-tests coverage-check analysis-config-check

verify: verify-fast publish audit image-scan helm-lint helm-template
