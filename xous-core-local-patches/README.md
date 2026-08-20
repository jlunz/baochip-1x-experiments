# Local patches for `sources/xous-core`

`sources/xous-core` is a gitignored, separately-tracked checkout of
`betrusted-io/xous-core` (`setup/fetch-sources.sh`, `--depth 1`). There is no
patch-series/refresh mechanism for it analogous to `linux/patches/` and
`tools/refresh-patches.sh` — these patches are not applied automatically by
anything in this repo yet. Reapply by hand after a fresh `fetch-sources.sh`:

```sh
git -C sources/xous-core apply "$PWD"/xous-core-local-patches/*.patch
```

| Patch | Why |
|---|---|
| `0001-pace-uf2-block-sends-at-1mbaud.patch` | `uf2send.py`'s block-transfer send is a single unpaced `ser.write()`, which reliably drops characters at 1 Mbaud with no flow control on this link (confirmed on hardware, `docs/04-bringup-log.md` 2026-08-20). Paces it to 5ms/char, matching the file's own existing `localecho off` workaround for the same symptom. Fine for a small image (E1); not viable as-is for the full kernel image's block count (G2) — see the patch's own comment. |
