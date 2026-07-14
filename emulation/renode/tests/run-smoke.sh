#!/usr/bin/env bash
# Build the bare-metal smoke test and run it through renode-test (Robot).
set -euo pipefail
TOP="$(cd "$(dirname "$0")/../../.." && pwd)"

make -C "$TOP/emulation/renode/tests/baremetal"
"$TOP/tools/renode/renode-test" "$TOP/emulation/renode/tests/smoke.robot" \
    --results-dir "$TOP/build/renode-smoke/results"
