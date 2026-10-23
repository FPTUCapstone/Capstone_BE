# Candidate pool replay

This network-free tool compares the current provider/matrix top-K policy with offline-only frozen
overflow, diversity, and cap-sensitivity experiments. Inputs must use opaque surrogate IDs and a
complete frozen directional duration matrix.

```powershell
dotnet run --project tools/TripMate.CandidatePoolReplay -- `
  --input tools/TripMate.CandidatePoolReplay/fixtures/synthetic-v1.jsonl `
  --manifest tools/TripMate.CandidatePoolReplay/fixtures/synthetic-v1.manifest.json `
  --output artifacts/candidate-pool-replay/synthetic-v1.result.json `
  --timing-output artifacts/candidate-pool-replay/synthetic-v1.timing.json
```

The result contains no timing or generated timestamp, so identical normalized inputs, manifest,
and code produce byte-identical output. The optional timing output is explicitly separate and
records its generation time, runtime/OS/CPU metadata, per-strategy sample count, elapsed
p50/p95/p99, and mean managed allocation. Strategy A calls the production selector implementations.
Strategies B/C/D are not referenced by production dependency injection.
