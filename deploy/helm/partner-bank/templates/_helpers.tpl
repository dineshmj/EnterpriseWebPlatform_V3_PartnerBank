{{/*
┌─ :: ARCHITECTURE :: One set of templates, three deployables ───────────────────────────────────
│ Why : The authorization server, the API and the window are deployed the same way (same security
│       context, probes, volumes, network rules); only their ports and settings differ. One
│       template per Kubernetes kind, ranging over .Values.components, keeps that sameness in one
│       place - the same shape as EWP's chart.
│ How : A component's key is its Service name. Public addresses keep the home PC's port numbers
│       (https://partner-auth.<domain>:47401): the issuer, the mutual-TLS endpoint aliases and the
│       port guards compare ports, so container port = Service port = public port. Setting values
│       are templates (tpl); secrets are only ever referenced, never rendered.
│ Ref : doc/Kubernetes.md | doc/Containers.md (run-time contract)
└────────────────────────────────────────────────────────────────────────────────────────────────
*/}}

{{/* A component's public address on one port: (list $ "partner-auth" 47401) -> https://partner-auth.<domain>:47401 */}}
{{- define "pb.public" -}}
{{- $root := index . 0 -}}
https://{{ include "pb.publicHost" (list $root (index . 1)) }}:{{ index . 2 }}
{{- end -}}

{{/* A component's public host name (no scheme, no port). */}}
{{- define "pb.publicHost" -}}
{{- $root := index . 0 -}}
{{ index . 1 }}.{{ required "global.domain is required (e.g. demo.example.com)" $root.Values.global.domain }}
{{- end -}}

{{- define "pb.image" -}}
{{- $g := .root.Values.global.image -}}
{{- $tag := required "global.image.tag is required: the full commit SHA the images were built from" $g.tag -}}
{{ $g.registry }}/{{ .c.image }}:{{ $tag }}
{{- end -}}

{{- define "pb.labels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/part-of: partner-bank
app.kubernetes.io/version: {{ .root.Values.global.image.tag | default "unset" | trunc 63 | quote }}
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .root.Chart.Name .root.Chart.Version }}
{{- end -}}

{{- define "pb.selector" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
{{- end -}}

{{/* The port the kubelet probes: (dict "c" $c) -> its number. */}}
{{- define "pb.probePort" -}}
{{- $want := .c.probePort -}}
{{- range .c.ports -}}{{- if eq .name $want -}}{{ .port }}{{- end -}}{{- end -}}
{{- end -}}

{{- define "pb.probeGet" -}}
path: {{ .path }}
port: {{ .port }}
scheme: HTTPS
httpHeaders:
  - name: Host
    value: {{ .host }}
{{- end -}}