# syntax=docker/dockerfile:1

# ---- build stage: compile and bake the index into the image ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/ ./src/
RUN dotnet publish src/Fraud.Api/Fraud.Api.csproj -c Release -o /app/publish \
        -r linux-x64 --self-contained false \
        -p:UseAppHost=false -p:PublishReadyToRun=true \
    && dotnet build src/Fraud.IndexBuilder/Fraud.IndexBuilder.csproj -c Release -o /app/builder

# reference data (references.json.gz baked at build time, never shipped at runtime as JSON)
COPY data/references.json.gz /data/references.json.gz
COPY data/normalization.json /app/publish/data/normalization.json
COPY data/mcc_risk.json      /app/publish/data/mcc_risk.json

ARG NLIST=2048
ARG TRAIN_SAMPLE=150000
ARG ITERS=15
RUN dotnet /app/builder/Fraud.IndexBuilder.dll \
        /data/references.json.gz \
        /app/publish/data/index.bin \
        ${NLIST} ${TRAIN_SAMPLE} ${ITERS}

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

ENV DATA_DIR=/app/data \
    PORT=8080 \
    DOTNET_gcServer=0 \
    DOTNET_GCConserveMemory=9 \
    DOTNET_TieredPGO=1

EXPOSE 8080
ENTRYPOINT ["dotnet", "Fraud.Api.dll"]
