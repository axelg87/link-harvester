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
# 2) Runtime stage: pure ASP.NET Core, no browser dependencies      #
###################################################################
FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true

RUN mkdir -p /app/data
VOLUME ["/app/data"]
EXPOSE 8080

ENTRYPOINT ["dotnet", "LinkHarvester.Api.dll"]
