#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image="${JELLYFIN_IMAGE:-jellyfin/jellyfin:10.11.11@sha256:aefb67e6a7ff1debdd154a78a7bbb780fd0c873d8639210a7f6a2016ad2b35db}"
archive="${1:-$(find "$root/artifacts/package" -maxdepth 1 -name 'aether-duplicate-finder-*.zip' -print -quit)}"
container="aether-dupfinder-smoke-${RANDOM}-${RANDOM}"
scratch="$(mktemp -d)"

cleanup() {
  status=$?
  trap - EXIT
  docker rm -f "$container" >/dev/null 2>&1 || true
  if [[ -d "$scratch" ]]; then
    docker run --rm --user 0:0 --entrypoint /bin/sh \
      --volume "$scratch:/scratch" "$image" -c 'rm -rf /scratch/*' >/dev/null 2>&1 || true
    rm -rf "$scratch" >/dev/null 2>&1 || true
  fi
  exit "$status"
}
trap cleanup EXIT

fail_with_logs() {
  docker logs "$container" >&2 || true
  echo "$1" >&2
  exit 1
}

if [[ -z "$archive" || ! -f "$archive" ]]; then
  echo "Final plugin archive is missing. Run tools/package-plugin.sh first." >&2
  exit 1
fi

archive_entries="$(unzip -Z1 "$archive" | LC_ALL=C sort)"
expected_entries='Jellyfin.Plugin.DuplicateFinder.dll'
if [[ "$archive_entries" != "$expected_entries" ]]; then
  echo "Unexpected install archive contents:" >&2
  echo "$archive_entries" >&2
  exit 1
fi

plugin_dir="$scratch/config/plugins/AETHER Duplicate Finder"
mkdir -p "$plugin_dir"
unzip -q "$archive" -d "$plugin_dir"
if [[ ! -s "$plugin_dir/Jellyfin.Plugin.DuplicateFinder.dll" ]]; then
  echo "The plugin archive was not installed into the smoke-test directory." >&2
  exit 1
fi
docker run --detach --name "$container" \
  --publish 127.0.0.1::8096 \
  --volume "$scratch/config:/config" \
  "$image" >/dev/null

port="$(docker port "$container" 8096/tcp | sed -n 's/.*://p')"
ready=false
for _ in $(seq 1 60); do
  if curl --connect-timeout 2 --max-time 5 --fail --silent \
    "http://127.0.0.1:${port}/System/Info/Public" > "$scratch/system-info.json"; then
    ready=true
    break
  fi
  if ! docker inspect --format '{{.State.Running}}' "$container" | grep -q true; then
    fail_with_logs "Jellyfin stopped before its public system endpoint became ready."
  fi
  sleep 2
done
if [[ "$ready" != "true" ]]; then
  fail_with_logs "Jellyfin did not become ready within 120 seconds."
fi

if ! jq -e '(.Version // .version) == "10.11.11"' "$scratch/system-info.json" >/dev/null; then
  fail_with_logs "The smoke container is not running Jellyfin 10.11.11."
fi

# Every DuplicateFinder endpoint requires Authorize(Roles=Administrator) — an
# unauthenticated call must be rejected, never silently allowed through.
groups_status=""
for _ in $(seq 1 60); do
  groups_status="$(curl --connect-timeout 2 --max-time 5 --silent \
    --output /dev/null --write-out '%{http_code}' \
    "http://127.0.0.1:${port}/DuplicateFinder/v1/groups" || true)"
  [[ "$groups_status" == "401" ]] && break
  if ! docker inspect --format '{{.State.Running}}' "$container" | grep -q true; then
    fail_with_logs "Jellyfin stopped before the plugin endpoint became ready."
  fi
  sleep 2
done
if [[ "$groups_status" != "401" ]]; then
  fail_with_logs "Expected the unauthenticated plugin endpoint to require auth (401), got $groups_status."
fi

startup_user_ready=false
for _ in $(seq 1 60); do
  if curl --connect-timeout 2 --max-time 5 --fail --silent \
    "http://127.0.0.1:${port}/Startup/User" > "$scratch/startup-user.json" \
    && jq -e 'has("Name")' "$scratch/startup-user.json" >/dev/null; then
    startup_user_ready=true
    break
  fi
  if ! docker inspect --format '{{.State.Running}}' "$container" | grep -q true; then
    fail_with_logs "Jellyfin stopped before its startup-user endpoint became ready."
  fi
  sleep 2
done
if [[ "$startup_user_ready" != "true" ]]; then
  fail_with_logs "Jellyfin's startup-user endpoint did not become ready within 120 seconds."
fi
username="$(jq -r '.Name' "$scratch/startup-user.json")"
password="$(jq -r '.Password // ""' "$scratch/startup-user.json")"
if ! curl --connect-timeout 2 --max-time 5 --fail --silent \
  --request POST \
  "http://127.0.0.1:${port}/Startup/Complete" >/dev/null; then
  fail_with_logs "Jellyfin rejected startup completion."
fi
auth_identity='MediaBrowser Client="AETHER%20Smoke", DeviceId="aether-ci", Device="CI", Version="0.1.0"'
jq -n --arg username "$username" --arg password "$password" \
  '{Username: $username, Pw: $password}' > "$scratch/auth-request.json"
if ! curl --connect-timeout 2 --max-time 5 --fail --silent \
  --request POST \
  --header "Authorization: $auth_identity" \
  --header 'Content-Type: application/json' \
  --data-binary "@$scratch/auth-request.json" \
  "http://127.0.0.1:${port}/Users/AuthenticateByName" > "$scratch/auth-response.json"; then
  fail_with_logs "Jellyfin rejected smoke-test authentication."
fi
access_token="$(jq -r '.AccessToken' "$scratch/auth-response.json")"
if [[ -z "$access_token" || "$access_token" == "null" ]]; then
  fail_with_logs "Jellyfin did not issue a smoke-test token."
fi
auth_header="$auth_identity, Token=$access_token"

# The startup user is a server administrator by default, so this authenticated call
# should now succeed and report an empty group list (nothing scanned yet).
if ! curl --connect-timeout 2 --max-time 5 --fail --silent \
  --header "Authorization: $auth_header" \
  "http://127.0.0.1:${port}/DuplicateFinder/v1/groups" > "$scratch/groups.json"; then
  fail_with_logs "The authenticated groups endpoint failed."
fi
if ! jq -e '. == []' "$scratch/groups.json" >/dev/null; then
  fail_with_logs "Expected an empty group list before any scan has run."
fi

logs="$(docker logs "$container" 2>&1)"
if grep -Eiq 'Jellyfin\.Plugin\.DuplicateFinder.*(exception|failed)' <<< "$logs"; then
  echo "$logs" >&2
  echo "Jellyfin reported a Duplicate Finder plugin error." >&2
  exit 1
fi

echo "Jellyfin 10.11.11 loaded the plugin, enforced admin auth, and returned an empty group list."
