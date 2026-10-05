#!/usr/bin/env bash
# Clones each pinned repo in reference-repos/repos.list fresh, runs `eolup
# scan` and `remediate` against it (once without any config, once with the
# optional .eolup.yml template applied if one is listed — simulating a real
# user having added one, since we can't commit a config into someone else's repo
# upstream), and prints the result for a human to compare against the documented
# expected outcome in reference-repos/README.md.
#
# Deliberately not an automated pass/fail check (unlike the fixture suite) —
# see reference-repos/README.md for why.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LIST="$REPO_ROOT/reference-repos/repos.list"
CLI="$REPO_ROOT/src/Eolup.Cli"

run_scan_and_remediate() {
  local dir="$1"
  echo "--- scan ---"
  dotnet run --project "$CLI" -- scan "$dir" || true
  echo
  echo "--- remediate ---"
  dotnet run --project "$CLI" -- remediate "$dir" || true
  echo
}

while IFS='|' read -r name url commit config; do
  [[ -z "$name" || "$name" == \#* ]] && continue

  echo "================================================================"
  echo "=== $name @ $commit"
  echo "================================================================"

  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT

  git clone --quiet "$url" "$tmp"
  git -C "$tmp" checkout --quiet "$commit"

  # Safety: a plain clone leaves 'origin' pointing at the real upstream repo. If
  # remediate ever returns HighConfidence, PullRequestPublisher would otherwise
  # attempt a real `git push` against someone else's actual GitHub repo — it
  # would fail (no write access), but it should never even be attempted.
  # Stripping the remote makes that path fail closed via the same "no remote
  # configured" message used everywhere else, rather than relying on a push
  # rejection as the safety net.
  git -C "$tmp" remote remove origin

  echo "--- without any .eolup.yml (repo's actual unmodified state) ---"
  run_scan_and_remediate "$tmp"

  if [[ -n "${config:-}" ]]; then
    cp "$REPO_ROOT/reference-repos/$config" "$tmp/.eolup.yml"
    echo "--- with .eolup.yml applied ($config) ---"
    run_scan_and_remediate "$tmp"
  fi

  rm -rf "$tmp"
  trap - EXIT
done < "$LIST"
