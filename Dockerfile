FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json KualiOnBase.Api.sln ./
COPY src/KualiOnBase.Api/KualiOnBase.Api.csproj src/KualiOnBase.Api/
RUN dotnet restore src/KualiOnBase.Api/KualiOnBase.Api.csproj
COPY src/ src/
RUN dotnet publish src/KualiOnBase.Api/KualiOnBase.Api.csproj \
      -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update \
 && apt-get install -y --no-install-recommends wget \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

RUN mkdir -p /data /backup /target \
 && chown -R app:app /app /data /backup /target
USER app

ENV ASPNETCORE_URLS=http://+:8080 \
    Database__Path=/data/kuali-onbase.db \
    Backup__RootPath=/backup

VOLUME ["/data", "/backup", "/target"]

EXPOSE 8080
ENTRYPOINT ["dotnet", "KualiOnBase.Api.dll"]
