# Phase B: Azure Container Apps image with the corpus baked in.
# Build context = repo root, with the data files staged under data/:
#   data/epo.db  data/models/  data/native/vec0.so
# If the image grows past ~10 GB, switch to an entrypoint that pulls
# epo.db/models from Blob Storage at startup instead of baking them in.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY EpoCaseLaw/ EpoCaseLaw/
RUN dotnet publish EpoCaseLaw -c Release -o /app

# aspnet (not runtime) image: required by the Microsoft.AspNetCore.App FrameworkReference.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY data/epo.db /data/epo.db
COPY data/models/ /data/models/
COPY data/native/vec0.so /data/native/vec0.so

ENV EPO_DATA_ROOT=/data \
    EPO_DB_PATH=/data/epo.db \
    ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

# EPO_MCP_API_KEY must be supplied at runtime (Container Apps secret).
ENTRYPOINT ["dotnet", "EpoCaseLaw.dll", "serve-http"]
