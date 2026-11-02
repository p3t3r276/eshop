#!/usr/bin/env bash
# Section status table, read from the front matter of docs/sections/*.md
#   bash scripts/status.sh         -> status table
#   bash scripts/status.sh ready   -> IDs ready to write (dependencies satisfied)
set -euo pipefail
cd "$(dirname "$0")/.."
shopt -s nullglob

declare -A st
declare -A file_of
total=0

for f in docs/sections/*.md; do
  id=$(awk -F': *' '/^id:/{print $2; exit}' "$f" | tr -d '[:space:]')
  s=$(awk -F': *' '/^status:/{print $2; exit}' "$f" | awk '{print $1}')
  [ -n "$id" ] || id="?"
  st["$id"]="${s:-?}"
  file_of["$id"]="$f"
done

if [ "${1:-}" = "ready" ]; then
  [ -f docs/PROGRESS.md ] || { echo "Missing docs/PROGRESS.md" >&2; exit 1; }
  drafts=()
  ready=()
  while IFS='|' read -r _ id _file _src _budget deps _; do
    id=$(echo "$id" | tr -d '[:space:]')
    [[ $id =~ ^S[0-9]+$ ]] || continue
    s="${st[$id]:-missing}"
    case "$s" in
      done|needs-fix) continue ;;
      draft) drafts+=("$id"); continue ;;
    esac
    deps=$(echo "$deps" | tr -d '[:space:]')
    ok=1
    if [ -n "$deps" ] && [ "$deps" != "-" ]; then
      IFS=',' read -ra arr <<< "$deps"
      for d in "${arr[@]}"; do
        [ "${st[$d]:-missing}" = "done" ] || ok=0
      done
    fi
    [ "$ok" = 1 ] && ready+=("$id")
  done < docs/PROGRESS.md
  for id in "${drafts[@]}" "${ready[@]}"; do echo "$id"; done
  exit 0
fi

printf '%-6s %-10s %7s  %s\n' ID STATUS WORDS FILE
for id in $(printf '%s\n' "${!st[@]}" | sort); do
  f="${file_of[$id]}"
  w=$(wc -w < "$f")
  printf '%-6s %-10s %7s  %s\n' "$id" "${st[$id]}" "$w" "$f"
  total=$((total + w))
done

if [ -f docs/PROGRESS.md ]; then
  while IFS='|' read -r _ id _rest; do
    id=$(echo "$id" | tr -d '[:space:]')
    [[ $id =~ ^S[0-9]+$ ]] || continue
    [ -n "${st[$id]:-}" ] || printf '%-6s %-10s %7s  %s\n' "$id" "missing" "-" "(no file yet)"
  done < docs/PROGRESS.md
fi
echo "TOTAL words: $total"
