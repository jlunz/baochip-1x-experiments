#!/usr/bin/env bash
# Regenerate linux/patches/v6.14/*.patch from the working kernel tree, or check
# that the two still agree.
#
#   tools/refresh-patches.sh            # regenerate the series from sources/linux
#   tools/refresh-patches.sh --check    # fail if the tree and the series differ
#
# Why this exists: sources/linux is gitignored, so it is scratch space that
# disappears with the container. Kernel-side fixes made there and not exported
# here are simply lost -- which is exactly what happened to the DUART earlycon
# and device-tree fixes after the first silicon session
# (docs/07-board-incident-2026-08-07.md). build-dabao-image.sh runs --check so
# an image can never be built from a tree that has drifted from the series.
set -euo pipefail

TOP="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$TOP/sources/linux"
OUT="$TOP/linux/patches/v6.14"
BASE="v6.14"

MODE="${1:-refresh}"

[ -d "$SRC/.git" ] || {
    echo "sources/linux not present; run setup/fetch-sources.sh" >&2
    exit 1
}

git -C "$SRC" rev-parse --verify --quiet "$BASE^{commit}" >/dev/null || {
    echo "sources/linux has no $BASE tag (shallow clone lost it?)" >&2
    exit 1
}

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# Same options the series was generated with: stable numbering, no signature
# block, no per-file rename detection surprises.
git -C "$SRC" format-patch --no-signature --zero-commit --no-numbered \
    -o "$TMP" "$BASE..HEAD" >/dev/null

case "$MODE" in
--check)
    # Compare the diffs only. Commit hashes, dates and blob index lines change
    # whenever the tree is rebuilt or a commit is amended, and none of that
    # affects the resulting kernel -- the point of the check is "did code get
    # fixed in sources/linux and never exported here", not "was the branch
    # rebased".
    rc=0
    python3 - "$OUT" "$TMP" <<'PYEOF' || rc=$?
import pathlib
import re
import sys


def bodies(d):
    out = {}
    for p in sorted(pathlib.Path(d).glob('*.patch')):
        text = p.read_text()
        i = text.find('diff --git ')
        body = text[i:] if i >= 0 else ''
        # blob hashes move with unrelated content; the +/- lines are the truth
        body = re.sub(r'^index [0-9a-f]+\.\.[0-9a-f]+', 'index', body,
                      flags=re.M)
        out[p.name] = body
    return out


have, want = bodies(sys.argv[1]), bodies(sys.argv[2])
if list(have) != list(want):
    print('series differs in file set:', file=sys.stderr)
    print('  committed:', ' '.join(have), file=sys.stderr)
    print('  generated:', ' '.join(want), file=sys.stderr)
    sys.exit(1)
drift = [n for n in have if have[n] != want[n]]
if drift:
    print('ERROR: these patches differ from sources/linux:', file=sys.stderr)
    for n in drift:
        print('  ' + n, file=sys.stderr)
    sys.exit(1)
print('patch series matches sources/linux (%d patches)' % len(have))
PYEOF
    if [ $rc -ne 0 ]; then
        echo >&2
        echo "Run tools/refresh-patches.sh to export the tree into the series," >&2
        echo "or rebuild the tree from $BASE + the series if the tree is stale." >&2
        exit 1
    fi
    ;;
refresh)
    rm -f "$OUT"/*.patch
    cp "$TMP"/*.patch "$OUT/"
    echo "refreshed $(ls -1 "$OUT"/*.patch | wc -l) patches into $OUT"
    git -C "$TOP" status --short "$OUT"
    ;;
*)
    echo "usage: $0 [--check]" >&2
    exit 2
    ;;
esac
