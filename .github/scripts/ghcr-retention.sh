#!/usr/bin/env bash
# ┌─ :: ARCHITECTURE + SECURITY :: GHCR retention: keep the last KEEP images of each package ──────
# │ Why : Every main push publishes a new version of each changed image; without pruning the
# │       registry only grows. Deleting the wrong version breaks an image that is still pinned.
# │ How : Per package in .github/container-images.json: the versions with a tag, newest first.
# │       The newest KEEP stay, and so does any version with a tag other than sha-<commit> (the
# │       branch tag "main" marks the current image). For each older one, its index is read
# │       from the registry and the version is deleted TOGETHER with the manifests it lists
# │       (the platform image and its SBOM / provenance attestation, which GHCR shows as
# │       untagged versions) - except a manifest a kept image lists too. An untagged version
# │       is never deleted on its own: it may belong to an image that stays.
# │       DRY_RUN=true only prints what would go.
# │       GitHub keeps a deleted version restorable for 30 days (package settings).
# │ Ref : doc/Containers.md "Registry retention" | OWASP A08
# └────────────────────────────────────────────────────────────────────────────────────────────────
set -euo pipefail

: "${OWNER:?}" "${KEEP:?}" "${DRY_RUN:?}"
owner=$(tr '[:upper:]' '[:lower:]' <<< "$OWNER")
case "$KEEP" in ''|*[!0-9]*) echo "KEEP must be a whole number" >&2; exit 1 ;; esac
if [ "$KEEP" -lt 3 ]; then echo "KEEP must be at least 3" >&2; exit 1; fi

# The manifests an image index lists (platform image, attestation); none for a single manifest.
manifests_of() {
  docker buildx imagetools inspect --raw "ghcr.io/$owner/$package@$1" | jq -r '.manifests[]?.digest'
}

{
  echo "### GHCR retention (keep $KEEP per image, dry run: $DRY_RUN)"
  echo '| Image | Versions with a tag | Kept | Deleted (with their manifests) |'
  echo '| --- | --- | --- | --- |'
} >> "$GITHUB_STEP_SUMMARY"

packages=$(jq -r '.images[].image' .github/container-images.json)
for package in $packages; do
  if ! versions=$(gh api --paginate "/users/$owner/packages/container/$package/versions?per_page=100" 2>/dev/null | jq -s 'add // []'); then
    echo "$package: not in the registry yet - skipped"
    continue
  fi

  # ARCH: a version stays when it is one of the newest KEEP with a tag, or carries a
  # non-commit tag (e.g. "main").
  drop=$(jq -c --argjson keep "$KEEP" '
    [ .[] | select(.metadata.container.tags | length > 0) ] | sort_by(.created_at) | reverse
    | to_entries
    | map(select(.key >= $keep and ([.value.metadata.container.tags[] | select(startswith("sha-") | not)] | length == 0)))
    | map(.value | {id, digest: .name, tags: .metadata.container.tags})' <<< "$versions")
  tagged=$(jq '[.[] | select(.metadata.container.tags | length > 0)] | length' <<< "$versions")
  count=$(jq 'length' <<< "$drop")

  # ┌─ SEC ──────────────────────────────────────────────────────────────────────────────────────
  # │ An image rebuilt from the cache can share its platform manifest with an older one. The
  # │ manifests listed by every image that STAYS are protected, so deleting an old index never
  # │ takes a manifest a kept image still needs.
  # └────────────────────────────────────────────────────────────────────────────────────────────
  protected=""
  if [ "$count" -gt 0 ]; then
    for digest in $(jq -r --argjson drop "$drop" '
        [.[] | select(.metadata.container.tags | length > 0) | .name] - [$drop[].digest] | .[]' <<< "$versions"); do
      protected="$protected $(manifests_of "$digest" | tr '\n' ' ')"
    done
  fi

  for row in $(jq -c '.[]' <<< "$drop"); do
    digest=$(jq -r '.digest' <<< "$row")
    ids=$(jq -r '.id' <<< "$row")
    for child in $(manifests_of "$digest"); do
      case " $protected " in *" $child "*) continue ;; esac
      id=$(jq -r --arg d "$child" '.[] | select(.name == $d) | .id' <<< "$versions")
      if [ -n "$id" ]; then ids="$ids $id"; fi
    done
    echo "$package: $(jq -r '.tags | join(",")' <<< "$row") -> versions $ids"
    if [ "$DRY_RUN" != "true" ]; then
      for id in $ids; do
        gh api --method DELETE "/users/$owner/packages/container/$package/versions/$id" --silent
      done
    fi
  done

  echo "| $package | $tagged | $((tagged - count)) | $count |" >> "$GITHUB_STEP_SUMMARY"
done