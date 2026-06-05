# Como submeter (passo a passo)

Substitua em todos os arquivos:
- `GHCR_USER` → seu usuário do GitHub (minúsculas) — em `submission/docker-compose.yml`
- `SEU_USUARIO` / `SEU_NOME` → em `info.json` e `LICENSE`

## 1. Publicar a imagem no GHCR (contém o index.bin)

A imagem é construída a partir de `data/references.json.gz` (não versionado) e já
embute o `index.bin`, então o startup é instantâneo.

```powershell
# PAT do GitHub com escopo write:packages
$env:CR_PAT = "<seu-token>"
./publish.ps1 -GhcrUser <seu-usuario>
```

Depois, no GitHub: **Profile → Packages → rinha-2026-fraud → Package settings →
Change visibility → Public**. E confirme a imagem em `submission/docker-compose.yml`:
`ghcr.io/<seu-usuario>/rinha-2026-fraud:latest`.

## 2. Repositório com duas branches

- `main` — código-fonte (todo o conteúdo de `fraud-detector/`, sem `data/`).
- `submission` — apenas: `docker-compose.yml`, `nginx.conf`, `info.json`
  (use os arquivos da pasta `submission/`). **Sem código-fonte.**

```sh
# branch submission deve ter apenas:
docker-compose.yml   # de submission/ (imagem pública hardcoded)
nginx.conf
info.json
```

## 3. PR de inscrição no repo da Rinha

No fork de `zanfranceschi/rinha-de-backend-2026`, adicione
`participants/<seu-usuario>.json`:

```json
[{ "id": "<seu-usuario>-csharp", "repo": "https://github.com/<seu-usuario>/rinha-de-backend-2026-csharp" }]
```

Abra o Pull Request. Licença MIT obrigatória (já incluída).

## 4. Rodar teste (prévia ou final)

Abra uma issue no repo da Rinha com `rinha/test` na descrição (opcionalmente
`rinha/test <id da submissão>`). A Engine roda, comenta o score e fecha a issue.
Use as prévias à vontade até o prazo: **2026-06-05T23:59:59.999-03:00**.

## Checklist de conformidade

- [x] Load balancer (nginx) round-robin → 2 instâncias de API
- [x] LB sem lógica de negócio (só proxy)
- [x] Porta 9999, rede `bridge`, sem `host`/`privileged`
- [x] Soma de limites: CPU 0.10+0.45+0.45 = **1.0**, memória 28+161+161 = **350MB**
- [x] Imagens públicas, `linux/amd64`
- [x] `GET /ready` (2xx quando pronto) e `POST /fraud-score`
- [x] Licença MIT
