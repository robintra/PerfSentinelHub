{{- define "perf-sentinel-hub.name" -}}perf-sentinel-hub{{- end }}
{{- define "perf-sentinel-hub.labels" -}}
app.kubernetes.io/name: {{ include "perf-sentinel-hub.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}
