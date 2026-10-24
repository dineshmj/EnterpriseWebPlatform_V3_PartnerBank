#!/usr/bin/env bash
# ┌─ :: ARCHITECTURE :: Check the Helm chart without a cluster ────────────────────────────────────
# │ Why : The same check in CI (.github/workflows/ci.yml, job "Helm chart") and on a laptop.
# │ How : helm lint + helm template for every values file, kubeconform (strict) on the result,
# │       and the required values must stop a render. kubeconform is skipped with a warning
# │       when it is not installed (laptop); CI always has it. The same script as EWP's
# │       deploy/helm/check-charts.sh, for one chart.
# │       Needs: helm, optionally kubeconform; KUBERNETES_VERSION and CRDS_CATALOG_COMMIT from
# │       the CI job (defaults below are the same).
# │ Ref : doc/Kubernetes.md
# └────────────────────────────────────────────────────────────────────────────────────────────────
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
chart="$here/partner-bank"
kubernetes_version="${KUBERNETES_VERSION:-1.34.0}"
crds_commit="${CRDS_CATALOG_COMMIT:-501527ab9c40c827144c8f45945b25a023e344ce}"
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT

validate() {
  if command -v kubeconform >/dev/null 2>&1; then
    kubeconform -strict -summary -kubernetes-version "$kubernetes_version" \
      -schema-location default \
      -schema-location "https://raw.githubusercontent.com/datreeio/CRDs-catalog/$crds_commit/{{.Group}}/{{.ResourceKind}}_{{.ResourceAPIVersion}}.json" \
      "$1"
  else
    echo "::warning::kubeconform not installed - schema check skipped for $1"
  fi
}

# Every environment's values file (values.yaml alone lacks the required values on purpose).
for values in "$chart"/values-*.yaml; do
  env_name="$(basename "$values" .yaml)"
  echo "== partner-bank with $env_name"
  helm lint --strict "$chart" -f "$values"
  helm template partner-bank "$chart" --namespace partner-bank -f "$values" > "$out/partner-bank-$env_name.yaml"
  validate "$out/partner-bank-$env_name.yaml"
done

# SEC: the chart refuses to render without the values that must come from the environment.
values="$chart/values-demo.yaml"
for missing in global.domain global.image.tag global.gateway.parentRefs; do
  if helm template partner-bank "$chart" -f "$values" --set "$missing=" > /dev/null 2>&1; then
    echo "::error::The partner-bank chart rendered without $missing - it must refuse."
    exit 1
  fi
  echo "refused without $missing (as it must)"
done

echo "Helm chart OK"