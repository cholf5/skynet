# syntax=docker/dockerfile:1

ARG DOTNET_VERSION=9.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

COPY nuget.config ./
COPY Directory.Build.props ./
COPY Directory.Build.targets ./
COPY Directory.Packages.props ./
COPY Skynet.sln ./
COPY src ./src
COPY docs ./docs

RUN dotnet restore Skynet.sln
RUN dotnet publish src/Skynet.Examples/Skynet.Examples.csproj \
--configuration Release \
--output /app/publish \
/p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION} AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Skynet.Examples.dll", "--gate"]
