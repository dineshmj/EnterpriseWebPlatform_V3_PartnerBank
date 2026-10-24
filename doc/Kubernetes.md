# Kubernetes deployment (Helm)

How the Partner Bank's three images ([Containers.md](Containers.md)) run in a Kubernetes cluster:
one Helm chart under [deploy/helm/partner-bank](../deploy/helm/partner-bank), what a cluster must
have before it, and every secret an environment must create.

In short: **one chart deploys the Partner Bank into its own namespace: one instance each,
non-root, read-only, no Kubernetes token. TLS is never ended in front of the Partner Bank - the
Gateway passes each connection through by its host name, so EWP's client certificate still
reaches the deployable. The API's internal port is open on the pod network to the window and the
authorization server only. The chart holds no secret: it names the Secrets it reads.** The same
shape as EWP's chart ([EWP doc/Kubernetes.md](https://github.com/dineshmj/EnterpriseWebPlatform_V3/blob/main/doc/Kubernetes.md)),
with the differences below (P2, P3, P4).

| Per deployable | What the chart makes |
|---|---|
| `partner-auth` (authorization server), `partner-api` (API), `partner-products` (window) | Deployment (1 instance, Recreate), Service (the same port numbers), PersistentVolumeClaim for `/data` (kept on uninstall), NetworkPolicy; per public port a TLSRoute |

## Decisions

| # | Decision | Why |
|---|---|---|
| P1 | **One chart, a list of components** ([values.yaml](../deploy/helm/partner-bank/values.yaml), `components:`); one template per Kubernetes kind ranges over it. A component's key is its Service name (`partner-api`) and, with the domain, its public host (`partner-api.<domain>`) | As EWP's K1/K2: the deployables are deployed the same way, reviewed once in the templates |
| P2 | **TLS passthrough** at the Gateway (Gateway API **TLSRoute**, standard channel): one Gateway listener per public port (protocol TLS, mode Passthrough, the same port number), the route chosen by the SNI host name | The pod must see EWP's client certificate in the handshake (mutual TLS authentication, certificate-bound tokens - Containers.md decision 4, Architecture D16). EWP's chart ends TLS at the Gateway instead (HTTPRoute + BackendTLSPolicy) |
| P3 | **Public addresses keep the home PC's port numbers** (`https://partner-auth.<domain>:47401`, `:47411`, `partner-api.<domain>:47403`, `partner-products.<domain>:47402`); container port = Service port = public port | The issuer, the mutual-TLS endpoint aliases and the port guards compare port numbers; no code change, and EWP's settings name the same ports everywhere |
| P4 | **The API's internal port (47404) is opened on the pod network by the chart, not the image** (`Kestrel__Endpoints__Internal__Url`), together with a NetworkPolicy that admits only the window and the authorization server; it has no route. The window and the consent page call `https://partner-api:47404` | Each deployable is its own pod, so "localhost only" (control 13) cannot hold; the rule moves from the socket to the network, and the image alone still never opens the port. Every `/internal` call that touches a customer still needs the customer's token (control 16). Not chosen: the window in the API's pod - two deployables would share a fate and a volume |
| P5 | **One instance each, Recreate, no PodDisruptionBudget** | SQLite, in-memory grants and handoffs, key files ([Containers.md](Containers.md), "Replicas"). A budget for one instance would only block node drains |
| P6 | **Default-deny NetworkPolicy, per port**: a public port admits only the Gateway's namespace; the internal port only its two callers. Nothing else in the cluster - EWP's namespace included - reaches a Partner Bank pod directly | Two organisations: EWP is an outside client, also in a shared demo cluster |
| P7 | **Probes on `/health/live` (start-up, liveness) and `/health/ready` (readiness: the SQLite database opens)**, over https on a port that never asks for a client certificate - the API: its internal port. A `Host` header with the Service name (`AllowedHosts` = the public host and the Service name) | The kubelet has no client certificate; the API's external port would refuse the handshake |
| P8 | **Same pod hardening as EWP**: UID 1654, read-only root file system with an emptyDir `/tmp`, no privilege escalation, all capabilities dropped, `RuntimeDefault` seccomp, no service account token, no service-link variables; the PKI Secret mounted read-only with only the files the deployable needs | Pod Security Standard "restricted" |
| P9 | **Secrets by name only**; **required values refuse to render** (the domain, the image tag, the Gateway) | As EWP's K5 and K10 |

## What a cluster needs first

| Prerequisite | Why |
|---|---|
| Kubernetes **1.34+** | The version the chart is checked against; `Chart.yaml` refuses older clusters |
| A **Gateway API** implementation with **TLSRoute** `gateway.networking.k8s.io/v1` (standard channel), and a Gateway with four TLS listeners in **Passthrough** mode: `partner-auth` (47401), `partner-auth-mtls` (47411), `partner-api` (47403), `partner-products` (47402) - the names are the ports' `listener` in values.yaml | P2, P3 |
| DNS: `partner-auth`, `partner-api`, `partner-products` `.<domain>` -> the Gateway's address | The deployables also call their authorization server by its public name (discovery, keys), from inside the cluster through the Gateway |
| A CNI that enforces **NetworkPolicy** (Azure CNI with Cilium on AKS) | P4, P6 |
| A storage class for three small ReadWriteOnce volumes | `/data` |

## Installing

```text
kubectl create namespace partner-bank
# create the two Secrets below (secret store / script)
helm install partner-bank deploy/helm/partner-bank -n partner-bank -f deploy/helm/partner-bank/values-demo.yaml \
  --set global.domain=<domain> --set global.image.tag=<full commit SHA>
```

[values-demo.yaml](../deploy/helm/partner-bank/values-demo.yaml) is the short-lived cloud demo,
next to EWP's in the same cluster; its host names are the ones EWP's `values-demo.yaml` calls
(`global.partnerBank`).

## Secrets an environment creates

| Secret | Keys | Used by |
|---|---|---|
| `partner-bank-pki` | `partner-bank-server.pfx`, `partner-bank-server.pfx.password`, `ewp-trust-root.cer`, `ewp-request-signing.cer` (the same files as `%USERPROFILE%\.ewp-pki`) | all three (the window: the first three). **The server certificate must name `partner-auth.<domain>`, `partner-api.<domain>`, `partner-products.<domain>` AND `partner-api`** - the internal address the window and the consent page check it against |
| `partner-bank-window-client` | `secret` | the authorization server (`PartnerBank__ProductsWindowClient__Secret`) and the window (`PartnerBank__WindowClientSecret`) - one value, so the two cannot drift |

EWP's code-flow redirect addresses (`global.ewp.redirectUris`) are optional: EWP's distributor
calls use client credentials only.

## Checking the chart

[check-charts.sh](../deploy/helm/check-charts.sh) - in CI (job "Helm chart", part of **CI
result**, [CI.md](CI.md)) and on a laptop with `helm` on the PATH: `helm lint --strict` and
`helm template` with every `values-*.yaml`, kubeconform (strict) against the Kubernetes 1.34
schemas and the Gateway API schemas, and a check that each required value still stops a render.
Nothing here needs a cluster or Docker.

## Known gaps

| Gap | Effect | Planned fix |
|---|---|---|
| Not yet installed on a cluster: checked by rendering and schemas only | Problems only a running cluster shows (the Gateway's passthrough listeners on high ports, a probe timing) are still to find | The first demo run (EWP steps 5-7) |
| In-cluster calls to the authorization server go out through the Gateway (hairpin) | One more hop for discovery and keys; needs the load balancer to allow it | A split-horizon DNS name or the Service address once the issuer and the Service share a name |
| No egress NetworkPolicy | A compromised pod can open connections out of the namespace | With the environment's addresses (Terraform step) |
| Kestrel reads the PKI files at start-up only | A renewed certificate is used after the next restart | A demo cluster lives hours; longer-lived: a restart on Secret change |
| One instance each (P5) | A pod restart is a short outage | Residual risk R3's production stores first |