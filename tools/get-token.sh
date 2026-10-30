#!/usr/bin/env bash
# Lấy access token từ Keycloak bằng client dev "shoplab-cli".
# Dùng:  TOKEN=$(./tools/get-token.sh an.nguyen)
#        curl -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/ping
set -euo pipefail

USERNAME="${1:-an.nguyen}"
PASSWORD="${2:-Passw0rd!}"
KEYCLOAK="${KEYCLOAK_URL:-http://localhost:8080}"

curl -s -X POST "$KEYCLOAK/realms/shoplab/protocol/openid-connect/token" \
    -H "Content-Type: application/x-www-form-urlencoded" \
    -d "grant_type=password" \
    -d "client_id=shoplab-cli" \
    -d "username=$USERNAME" \
    --data-urlencode "password=$PASSWORD" \
    -d "scope=openid" | python3 -c 'import sys, json; print(json.load(sys.stdin)["access_token"])'
