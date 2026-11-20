#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")"
export ASPNETCORE_ENVIRONMENT=Production
if [[ -n "${SDATA_FILE:-}" ]]; then
  [[ -f "$SDATA_FILE" ]] || { echo "SDATA_FILE does not exist" >&2; exit 1; }
elif [[ -f configs/system.json ]]; then
  export SDATA_FILE="$PWD/configs/system.json"
else
  password="${SDATA_INITIAL_ADMIN_PASSWORD:-}"
  if [[ -z "$password" && -t 0 ]]; then
    read -r -s -p "Initial admin password (at least 12 characters): " password
    printf '\n'
  fi
  [[ ${#password} -ge 12 ]] || { echo "Set SDATA_INITIAL_ADMIN_PASSWORD (at least 12 characters)" >&2; exit 1; }
  escaped="${password//\\/\\\\}"
  escaped="${escaped//\"/\\\"}"
  escaped="${escaped//$'\n'/\\n}"
  escaped="${escaped//$'\r'/\\r}"
  escaped="${escaped//$'\t'/\\t}"
  template="$(< configs/system.example.json)"
  marker='"InitialAdminPassword": ""'
  [[ "$template" == *"$marker"* ]] || { echo "Invalid config template" >&2; exit 1; }
  umask 077
  # noclobber prevents replacing a config created by another first-start process.
  (set -o noclobber; printf '%s"InitialAdminPassword": "%s"%s\n' "${template%%"$marker"*}" "$escaped" "${template#*"$marker"}" > configs/system.json)
  export SDATA_FILE="$PWD/configs/system.json"
fi
unset SDATA_INITIAL_ADMIN_PASSWORD password escaped
exec ./SData "$@"
