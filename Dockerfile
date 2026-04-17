# ─── Build stage ───
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy only the Api project graph for a layer-cached restore. The solution-wide
# restore would also pull in test projects that aren't shipped in the runtime image.
COPY src/ContractEngine.Api/ContractEngine.Api.csproj src/ContractEngine.Api/
COPY src/ContractEngine.Core/ContractEngine.Core.csproj src/ContractEngine.Core/
COPY src/ContractEngine.Infrastructure/ContractEngine.Infrastructure.csproj src/ContractEngine.Infrastructure/
COPY src/ContractEngine.Jobs/ContractEngine.Jobs.csproj src/ContractEngine.Jobs/

# Restore dependencies for the Api and its transitive project refs (cached layer).
RUN dotnet restore src/ContractEngine.Api/ContractEngine.Api.csproj

# Copy the full src/ tree and publish the Api (tests excluded by .dockerignore).
COPY src/ src/
RUN dotnet publish src/ContractEngine.Api/ContractEngine.Api.csproj -c Release -o /app/publish --no-restore

# ─── Runtime stage ───
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Create document storage directory
RUN mkdir -p /app/data/documents

# Copy published output
COPY --from=build /app/publish .

# Expose port
EXPOSE 5000

# Health check
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD curl -f http://localhost:5000/health || exit 1

ENTRYPOINT ["dotnet", "ContractEngine.Api.dll"]
