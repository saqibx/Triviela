# Build context is the repo root (see fly.toml [build].dockerfile).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/Triviela.Relay/Triviela.Relay.csproj
RUN dotnet publish src/Triviela.Relay/Triviela.Relay.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Triviela.Relay.dll"]
