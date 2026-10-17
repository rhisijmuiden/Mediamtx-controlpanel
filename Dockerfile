FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/MediaMtxPanel/MediaMtxPanel.csproj src/MediaMtxPanel/
RUN dotnet restore src/MediaMtxPanel/MediaMtxPanel.csproj
COPY src/ src/
# Commit shown under Info and used by the update check: docker compose build --build-arg GIT_COMMIT=$(git rev-parse HEAD)
ARG GIT_COMMIT=""
RUN dotnet publish src/MediaMtxPanel/MediaMtxPanel.csproj -c Release -o /app --no-restore -p:SourceRevisionId="$GIT_COMMIT"

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__DefaultConnection="DataSource=/app/data/panel.db;Cache=Shared" \
    Panel__DataProtectionKeysPath=/app/data/keys
RUN mkdir -p /app/data && chown -R $APP_UID /app/data
VOLUME /app/data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "MediaMtxPanel.dll"]
