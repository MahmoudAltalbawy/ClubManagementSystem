FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY SportsPlatform.Auth.sln ./
COPY SportsPlatform.Auth.Core/SportsPlatform.Auth.Core.csproj SportsPlatform.Auth.Core/
COPY SportsPlatform.Auth.Infrastructure/SportsPlatform.Auth.Infrastructure.csproj SportsPlatform.Auth.Infrastructure/
COPY SportsPlatform.Auth.Api/SportsPlatform.Auth.Api.csproj SportsPlatform.Auth.Api/
RUN dotnet restore SportsPlatform.Auth.Api/SportsPlatform.Auth.Api.csproj -m:1

COPY . .
RUN dotnet publish SportsPlatform.Auth.Api/SportsPlatform.Auth.Api.csproj -c Release -o /app/publish --no-restore -m:1

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:5122
EXPOSE 5122

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "SportsPlatform.Auth.Api.dll"]
