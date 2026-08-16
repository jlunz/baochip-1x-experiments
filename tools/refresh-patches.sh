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
# (docs/07-board-incident-2026-08-07.md). build-dabao-image.sh runs --check
# before building a kernel-bearing image, so one can never be built from a tree
# that has drifted from the series.
set -euo pipefail

TOP="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$TOP/sources/linux"
OUT="$TOP/linux/patches/v6.14"
BASE="v6.14"
# The series lives on this branch in sources/linux (docs/03-status-and-resume.md).
# Override for a differently-named working branch.
SERIES_REF="${SERIES_REF:-bao1x-xip-fixes}"

MODE="${1:-refresh}"
case "$MODE" in
--check | refresh) ;;
*)
    echo "usage: $0 [--check]" >&2
    exit 2
    ;;
esac

[ -d "$SRC/.git" ] || {
    echo "sources/linux not present; run setup/fetch-sources.sh" >&2
    exit 1
}

git -C "$SRC" rev-parse --verify --quiet "$BASE^{commit}" >/dev/null || {
    echo "sources/linux has no $BASE tag (shallow clone lost it?)" >&2
    exit 1
}

# Resolve the series ref, falling back to HEAD for a detached working tree.
if git -C "$SRC" rev-parse --verify --quiet "$SERIES_REF^{commit}" >/dev/null; then
    TIP="$SERIES_REF"
else
    TIP="HEAD"
fi

# A tree sitting exactly on the base tag has no series applied. Catching this
# here matters: fetch-sources.sh clones with --depth 1 --branch v6.14, so a
# fresh container looks precisely like this, and the refresh path below is
# destructive.
if [ "$(git -C "$SRC" rev-parse "$TIP^{commit}")" = "$(git -C "$SRC" rev-parse "$BASE^{commit}")" ]; then
    echo "sources/linux is at $BASE with no patches applied (looked for '$SERIES_REF')." >&2
    echo "Apply the series first:  git -C sources/linux checkout -b $SERIES_REF $BASE" >&2
    echo "                         git -C sources/linux am $OUT/*.patch" >&2
    exit 1
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# Match how the committed series was produced: numbered subjects ([PATCH nn/22],
# which docs/03 phase 6 depends on for review order) and real commit hashes.
# --no-signature is the one deliberate difference: the trailer carries the local
# git version, which would otherwise churn the series on every machine. The
# comparator below strips it from both sides so the committed files, which still
# carry one, compare equal.
git -C "$SRC" format-patch --no-signature -o "$TMP" "$BASE..$TIP" >/dev/null

GENERATED=$(find "$TMP" -name '*.patch' | wc -l)
[ "$GENERATED" -gt 0 ] || {
    echo "git format-patch $BASE..$TIP produced no patches; refusing to touch $OUT" >&2
    exit 1
}

compare() {
    python3 - "$OUT" "$TMP" <<'PYEOF'
import pathlib
import re
import sys


def bodies(d):
    """Diff text only, normalised so cosmetics cannot masquerade as drift."""
    out = {}
    for p in sorted(pathlib.Path(d).glob('*.patch')):
        text = p.read_text()
        i = text.find('diff --git ')
        body = text[i:] if i >= 0 else ''
        # git's signature trailer carries the local git version, and
        # --no-signature omits it entirely; it is not part of the diff.
        body = re.sub(r'\n-- \n\S*\n?$', '\n', body)
        # blob hashes move with unrelated content; the +/- lines are the truth
        body = re.sub(r'^index [0-9a-f]+\.\.[0-9a-f]+', 'index', body,
                      flags=re.M)
        out[p.name] = body
    return out


have, want = bodies(sys.argv[1]), bodies(sys.argv[2])
if list(have) != list(want):
    print('series differs in file set:', file=sys.stderr)
    print('  committed: %d patches' % len(have), file=sys.stderr)
    print('  generated: %d patches' % len(want), file=sys.stderr)
    for n in sorted(set(have) ^ set(want)):
        print('  only in one side: ' + n, file=sys.stderr)
    sys.exit(1)
drift = [n for n in have if have[n] != want[n]]
if drift:
    print('ERROR: these patches differ from sources/linux:', file=sys.stderr)
    for n in drift:
        print('  ' + n, file=sys.stderr)
    sys.exit(1)
print('patch series matches sources/linux (%d patches)' % len(have))
PYEOF
}

case "$MODE" in
--check)
    # Compare the diffs only. Commit hashes, dates and blob index lines change
    # whenever the tree is rebuilt or a commit is amended, and none of that
    # affects the resulting kernel -- the point of the check is "did code get
    # fixed in sources/linux and never exported here", not "was the branch
    # rebased".
    rc=0
    compare || rc=$?
    if [ $rc -ne 0 ]; then
        echo >&2
        echo "Run tools/refresh-patches.sh to export the tree into the series," >&2
        echo "or rebuild the tree from $BASE + the series if the tree is stale." >&2
        exit 1
    fi
    ;;
refresh)
    # Stage the new series completely before removing the old one: a partial
    # regeneration must never be able to leave $OUT emptier than it found it.
    STAGE="$TMP/stage"
    mkdir -p "$STAGE"
    cp "$TMP"/*.patch "$STAGE/"
    rm -f "$OUT"/*.patch
    cp "$STAGE"/*.patch "$OUT/"
    echo "refreshed $GENERATED patches into $OUT (from $TIP)"
    git -C "$TOP" status --short "$OUT"
    ;;
esac
