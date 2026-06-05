# Rinha de Backend 2026 — Fraud Detection (C# / .NET 9)

Fraud-detection backend for [Rinha de Backend 2026](https://github.com/zanfranceschi/rinha-de-backend-2026).
For each card transaction it builds a 14-dimension vector, finds the 5 nearest
reference vectors (k-NN) and returns `fraud_score = frauds / 5`,
`approved = fraud_score < 0.6`.

## Approach

| Concern | Decision |
|---|---|
| Vector search | **IVF** (k-means coarse quantizer, 2048 lists) — exact brute force over 3M×14 is too slow |
| Memory | Vectors **quantized to int8** (`[-1,1] → [0,255]`): 42 MB instead of 168 MB, fits the 350 MB budget |
| Index build | Done **once at image-build time** (`Fraud.IndexBuilder`) and baked into the image → `/ready` in ms |
| Responses | Only 6 distinct outputs (`frauds/5`) — pre-serialized, zero per-request JSON writing |
| Topology | nginx round-robin → 2 .NET API replicas |

Measured locally against the official `test-data.json` (54,100 labelled payloads):
~0.4% failure rate at `nprobe=16`, well under the 15% cut.

## Projects

- `src/Fraud.Core` — vectorization (14 dims), int8 quantizer, IVF index format, k-NN searcher.
- `src/Fraud.IndexBuilder` — reads `references.json.gz`, trains k-means, writes the compact `index.bin`.
- `src/Fraud.Api` — minimal API exposing `GET /ready` and `POST /fraud-score`.
- `src/Fraud.Validate` — offline accuracy/score harness against `test-data.json`.

## Build & run locally

```sh
# 1. place the reference files under ./data
#    data/references.json.gz, data/normalization.json, data/mcc_risk.json

# 2. build the image (compiles + bakes index.bin)
docker build -t fraud-api:local .

# 3. run nginx + 2 API replicas on port 9999
docker compose up
```

`GET http://localhost:9999/ready` → 200, `POST http://localhost:9999/fraud-score`.

## Configuration (env)

| Var | Default | Meaning |
|---|---|---|
| `NPROBE` | 24 | IVF clusters scanned per query (recall/latency trade-off) |
| `DATA_DIR` | `/app/data` | location of `index.bin`, `normalization.json`, `mcc_risk.json` |
| `PORT` | 8080 | API listen port (nginx upstream) |

## License

MIT — see [LICENSE](./LICENSE).
