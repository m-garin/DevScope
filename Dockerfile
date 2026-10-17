FROM node:24-alpine AS web-build
WORKDIR /src/web
COPY src/DevScope.Web/package*.json ./
RUN npm ci
COPY src/DevScope.Web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src
COPY src/DevScope.Api/DevScope.Api.csproj src/DevScope.Api/
COPY data/mock-data.json data/mock-data.json
RUN dotnet restore src/DevScope.Api/DevScope.Api.csproj
COPY src/DevScope.Api/ src/DevScope.Api/
COPY --from=web-build /src/web/dist src/DevScope.Api/wwwroot
RUN dotnet publish src/DevScope.Api/DevScope.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=api-build /app/publish .
COPY docker-entrypoint.sh /usr/local/bin/devscope-entrypoint
RUN chmod +x /usr/local/bin/devscope-entrypoint
RUN mkdir -p /app/config /app/data
EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/devscope-entrypoint"]

