#!/usr/bin/env bash
# Driver for the MCAROC.Portal ASP.NET app: build / start / smoke / stop.
# Run from Git Bash (this repo's default shell) on the dev Windows machine.
# Usage:
#   bash .claude/skills/run-mcaroc-portal/smoke.sh build
#   bash .claude/skills/run-mcaroc-portal/smoke.sh start   # backgrounds itself, returns once healthy
#   bash .claude/skills/run-mcaroc-portal/smoke.sh smoke   # drives real routes, prints PASS/FAIL per check
#   bash .claude/skills/run-mcaroc-portal/smoke.sh stop
set -uo pipefail

PORT="${PORT:-5219}"
SKILL_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="$(cd "$SKILL_DIR/../../../MCAROC.Portal/MCAROC_Analysis" && pwd)"
LOG_FILE="${LOG_FILE:-/tmp/mcaroc-portal.log}"
BASE="http://localhost:$PORT"

# The app's own PID is not the bash job id: it's a real Windows child process, found by the
# port it's listening on. This is what `stop` and the "already running" check in `start` use.
find_pid() {
  powershell -NoProfile -Command \
    "(Get-NetTCPConnection -LocalPort $PORT -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty OwningProcess)" \
    2>/dev/null | tr -d '\r' | tr -d '[:space:]'
}

cmd="${1:-help}"

case "$cmd" in
  build)
    (cd "$APP_DIR" && dotnet build --nologo)
    ;;

  start)
    existing="$(find_pid)"
    if [ -n "$existing" ]; then
      echo "Already listening on $BASE (PID $existing) — nothing to do."
      exit 0
    fi
    echo "Launching (log: $LOG_FILE)…"
    # IMPORTANT: call this `start` subcommand as an ordinary FOREGROUND command from your
    # shell/tool. It backgrounds the actual dotnet process itself (with & + disown) and returns
    # in a few seconds once the app answers — don't ALSO wrap this call in your own
    # background-job feature (e.g. a tool's run_in_background), or the two backgrounding layers
    # fight each other: the outer wrapper can report "exited" while the inner dotnet process is
    # still alive and orphaned, holding the port with no tracked handle to it. (Hit exactly this
    # once while building this skill — see Gotchas in SKILL.md.)
    (
      cd "$APP_DIR" && \
      ASPNETCORE_ENVIRONMENT=Development dotnet run --no-build --nologo > "$LOG_FILE" 2>&1 &
      disown
    )
    for _ in $(seq 1 40); do
      code="$(curl -s -o /dev/null -w '%{http_code}' "$BASE/" 2>/dev/null || true)"
      if [ "$code" = "200" ]; then
        pid="$(find_pid)"
        echo "Up on $BASE (PID $pid)."
        exit 0
      fi
      sleep 1
    done
    echo "Did not come up within 40s — check $LOG_FILE" >&2
    tail -n 40 "$LOG_FILE" >&2 2>/dev/null
    exit 1
    ;;

  smoke)
    fail=0
    check() { # check <label> <expected-status> <curl-args...>
      local label="$1" expected="$2"; shift 2
      local got
      got="$(curl -s -o /tmp/smoke_body.$$ -w '%{http_code}' "$@")"
      if [ "$got" = "$expected" ]; then
        echo "PASS  $label -> $got"
      else
        echo "FAIL  $label -> got $got, expected $expected"
        fail=1
      fi
    }

    check "home page"                200 "$BASE/"
    check "requests list"            200 "$BASE/Requests"
    check "auto-fetch form"          200 "$BASE/Requests/AutoFetch"
    check "nonexistent request"      404 "$BASE/Requests/999999"
    check "auth-gated route redirects, not 500" 302 "$BASE/registry"
    check "autofetch blank-CIN POST is rejected" 400 -X POST "$BASE/Requests/AutoFetch" -d "Cin=" -d "ClientId=1"

    # Pick a real, already-analysed request from the dev DB (if any) and exercise its details
    # page + dossier PDF — the one check above alone can't prove: real data renders correctly.
    req_id="$(sqlcmd -S ".\\SQLEXPRESS" -d MCAROC_Analysis -h -1 -W \
      -Q "SET NOCOUNT ON; SELECT TOP 1 RequestId FROM Requests WHERE RequestStatus IN ('AnalysisCompleted','DataExtracted') ORDER BY RequestId DESC" \
      2>/dev/null | tr -d '[:space:]')"
    if [ -n "$req_id" ] && [ "$req_id" != "NULL" ]; then
      check "details page for request $req_id" 200 "$BASE/Requests/$req_id"
      curl -s -o /tmp/smoke_dossier.$$ -w 'dossier status=%{http_code} content-type=%{content_type}\n' \
        "$BASE/Requests/$req_id/dossier"
      if command -v file >/dev/null 2>&1 && file /tmp/smoke_dossier.$$ | grep -q "PDF document"; then
        echo "PASS  dossier renders as a real PDF"
      else
        echo "FAIL  dossier did not come back as a PDF"
        fail=1
      fi
      rm -f /tmp/smoke_dossier.$$
    else
      echo "SKIP  no AnalysisCompleted/DataExtracted request in the dev DB — dossier check skipped"
    fi
    rm -f /tmp/smoke_body.$$

    exit "$fail"
    ;;

  stop)
    pid="$(find_pid)"
    if [ -n "$pid" ]; then
      taskkill //PID "$pid" //F
    else
      echo "Nothing listening on port $PORT."
    fi
    ;;

  *)
    echo "usage: $0 {build|start|smoke|stop}"
    exit 2
    ;;
esac
