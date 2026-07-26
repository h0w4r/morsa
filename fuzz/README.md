# Morsa parser fuzzing and hardening

This directory contains Morsa 1.0's dependency-free, deterministic mutation harness. It exercises the production magic-byte inspector and the ZIP/XML, PDF, SVG, RDP, ICA, and binary metadata extractors. Every hostile input runs in a disposable child process.

## Quick start on Linux

```bash
bash ./fuzz/scripts/verify-corpus.sh
bash ./fuzz/scripts/smoke.sh
MORSA_FUZZ_TOTAL_SECONDS=900 bash ./fuzz/scripts/run-bounded.sh
```

For an uninterrupted campaign made of finite, reproducible rounds:

```bash
MORSA_FUZZ_TARGET=all \
MORSA_FUZZ_CHUNK_SECONDS=900 \
bash ./fuzz/scripts/run-continuous.sh
```

See [README.es.md](README.es.md) for the complete target matrix, resource budgets, exit codes, output schema, corpus maintenance, triage, and reproduction workflow.

## Safety boundaries

- Input defaults to 1 MiB and is rejected before parsing when oversized.
- ZIP expansion is capped at 16 times the input size and never above 64 MiB.
- ZIP entry count is capped at 2,000.
- Each parser has a cooperative timeout and an external process watchdog.
- Linux scripts add CPU and virtual-memory limits and use GNU `timeout` when available.
- Worker stdout/stderr and result collection sizes are bounded.
- Findings are stored under ignored `fuzz/artifacts/` and include a SHA-256 plus an exact reproduction command.

## Direct deterministic run

```bash
dotnet build ./fuzz/Morsa.FuzzHarness/Morsa.FuzzHarness.csproj -c Release
dotnet ./fuzz/Morsa.FuzzHarness/bin/Release/net10.0/Morsa.FuzzHarness.dll \
  --target all \
  --iterations 1000 \
  --timeout-ms 2000 \
  --max-input-bytes 1048576 \
  --max-total-seconds 300 \
  --seed 1297044051
```

Exit code `0` means no finding. Exit code `1` means at least one crash, timeout, native failure, or result-contract violation was preserved for triage.
