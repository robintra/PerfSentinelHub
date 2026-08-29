NATIVE_RIDS := linux-x64 linux-arm64 osx-arm64 win-x64
OUTPUT ?= dist
override COVERAGE_DIR := artifacts/coverage
override COVERAGE_REPORT := $(COVERAGE_DIR)/coverage.cobertura.xml
override COVERAGE_RAW := $(COVERAGE_DIR)/raw.cobertura.xml
override SONAR_DIR := artifacts/sonar

.PHONY: tool-restore restore format build coverage coverage-check analysis-config-check badge-check security-exceptions security sonar-prepare python-tests js-tests test publish package-native audit backup image image-scan helm-lint helm-template release-check verify-fast verify

tool-restore:
	dotnet tool restore

restore:
	dotnet restore PerfSentinelHub.sln --locked-mode

format: restore
	dotnet format PerfSentinelHub.sln --verify-no-changes --no-restore

build: restore
	dotnet build PerfSentinelHub.sln -c Release --no-restore --warnaserror

coverage: tool-restore build
	rm -rf "$(COVERAGE_DIR)" TestResults
	mkdir -p "$(COVERAGE_DIR)"
	dotnet test PerfSentinelHub.sln -c Release --no-build --no-restore -- --coverage --coverage-output-format cobertura --coverage-output "$(CURDIR)/$(COVERAGE_RAW)" --report-trx --report-trx-filename tests.trx
	@test -f "$(COVERAGE_RAW)" || { echo "expected a Cobertura report at $(COVERAGE_RAW)" >&2; exit 1; }
	dotnet tool run reportgenerator -- -reports:"$(COVERAGE_RAW)" -targetdir:"$(COVERAGE_DIR)" -reporttypes:Cobertura "-filefilters:-**/obj/**"
	python3 scripts/normalize-coverage.py "$(COVERAGE_DIR)/Cobertura.xml" "$(COVERAGE_REPORT)"
	rm -f "$(COVERAGE_DIR)/Cobertura.xml" "$(COVERAGE_RAW)"
	mv TestResults/tests.trx "$(COVERAGE_DIR)/tests.trx"

coverage-check: coverage
	python3 scripts/check-coverage.py --current-report "$(COVERAGE_REPORT)"

analysis-config-check:
	python3 scripts/check-analysis-config.py

badge-check:
	python3 scripts/check-badges.py

security-exceptions:
	python3 scripts/check-security-exceptions.py

security: security-exceptions analysis-config-check audit
	python3 scripts/check-supply-chain.py

sonar-prepare: analysis-config-check tool-restore coverage
	rm -rf "$(SONAR_DIR)"
	dotnet tool run reportgenerator -- -reports:"$(COVERAGE_REPORT)" -targetdir:"$(SONAR_DIR)" -reporttypes:SonarQube
	python3 scripts/check-analysis-config.py --require-analysis-inputs

python-tests:
	python3 -m unittest discover -s scripts/tests

# The launcher's command builders, then the same builders against the real
# engine. Node's own runner, no package and no build step, the way launcher.js
# itself is authored. The second file skips itself when there is no engine
# build to run, so this target needs nothing that the first one did not.
js-tests:
	node --test tests/launcher.test.js tests/launcher-e2e.test.js

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

backup: build
	@test -n "$(DEST)" || { echo "DEST is required (backup file to create)" >&2; exit 2; }
	Hub__DatabasePath="$${DB:-/data/hub.db}" dotnet run --project PerfSentinelHub/PerfSentinelHub.csproj -c Release --no-build --no-restore -- backup "$(DEST)"

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

verify-fast: tool-restore format python-tests js-tests coverage-check analysis-config-check badge-check

verify: verify-fast publish audit image-scan helm-lint helm-template
