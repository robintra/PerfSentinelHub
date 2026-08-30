{{- define "perf-sentinel-hub.name" -}}perf-sentinel-hub{{- end }}
{{- define "perf-sentinel-hub.labels" -}}
app.kubernetes.io/name: {{ include "perf-sentinel-hub.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}
{{- define "perf-sentinel-hub.image" -}}
{{- $repositoryPattern := "^(?:[a-z0-9]+(?:[.-][a-z0-9]+)*(?::[0-9]+)?/)?[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*$" -}}
{{- if not (regexMatch $repositoryPattern .Values.image.repository) -}}
{{- fail "image.repository must contain neither a tag nor a digest" -}}
{{- end -}}
{{- if not (regexMatch "^sha256:[0-9a-f]{64}$" .Values.image.digest) -}}
{{- fail "image.digest must be an immutable sha256 digest" -}}
{{- end -}}
{{- if eq .Values.image.digest (printf "sha256:%s" (repeat 64 "0")) -}}
{{- fail "image.digest is still the unstamped placeholder. Pass --set image.digest, or install the published chart, which carries a stamped one" -}}
{{- end -}}
{{- printf "%s@%s" .Values.image.repository .Values.image.digest -}}
{{- end }}
