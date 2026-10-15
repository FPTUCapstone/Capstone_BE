# TM-70 semantic search PoC plan

1. Write Application tests first, record red, then feature scoped orchestration/ports + Infrastructure HTTP adapters. Focused dotnet tests, Release build, parent two-stage review.
2. Write Python protocol and evaluation tests first, then singleton CPU inference, deterministic synthetic fixtures/index/evaluation. Run unittest; attempt real model smoke/Qdrant integration and report failures honestly.
3. Separate docker-compose.semantic.yml, operations guide and SQL disposable harness demo test. Coordinate SQL verification, changed formatting, diff/secret review. No production route wiring or schema edits.
