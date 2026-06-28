FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/LlmSandboxApi/LlmSandboxApi.csproj src/LlmSandboxApi/
RUN dotnet restore src/LlmSandboxApi/LlmSandboxApi.csproj
COPY src/LlmSandboxApi/ src/LlmSandboxApi/
RUN dotnet publish src/LlmSandboxApi/LlmSandboxApi.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl \
 && rm -rf /var/lib/apt/lists/*
RUN mkdir -p /app/bin
COPY --from=build /out /app/bin
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_CONTENTROOT=/app/bin \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "/app/bin/LlmSandboxApi.dll"]
