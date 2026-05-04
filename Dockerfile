###################################################################
# 1) Build stage: restore + publish a self-contained-ish release    #
###################################################################
FROM mcr.microsoft.com/dotnet/sdk:8.0-noble AS build
WORKDIR /src

COPY LinkHarvester.sln ./
COPY src/ src/
COPY tests/ tests/

RUN dotnet restore LinkHarvester.sln
RUN dotnet publish src/LinkHarvester.Api/LinkHarvester.Api.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

###################################################################
# 2) Runtime stage: ASP.NET Core + Playwright Chromium dependencies #
###################################################################
FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble AS runtime
WORKDIR /app

# Playwright Chromium dependencies. Keeping the list explicit so we know
# exactly what we drag in; avoids a 1GB+ "playwright install --with-deps" surprise.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates curl \
        libnss3 libnspr4 libatk1.0-0t64 libatk-bridge2.0-0t64 libcups2t64 \
        libdrm2 libxkbcommon0 libxcomposite1 libxdamage1 libxfixes3 libxrandr2 \
        libgbm1 libpango-1.0-0 libcairo2 libasound2t64 libatspi2.0-0t64 \
        fonts-liberation \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    PLAYWRIGHT_BROWSERS_PATH=/app/.playwright-browsers

# Ship a stable data dir for SQLite + Playwright user profile.
RUN mkdir -p /app/data /app/.playwright-browsers

VOLUME ["/app/data"]
EXPOSE 8080

ENTRYPOINT ["dotnet", "LinkHarvester.Api.dll"]
