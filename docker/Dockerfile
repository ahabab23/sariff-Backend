FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["src/SARIFF.Core/SARIFF.Core.csproj", "src/SARIFF.Core/"]
COPY ["src/SARIFF.Infrastructure/SARIFF.Infrastructure.csproj", "src/SARIFF.Infrastructure/"]
COPY ["src/SARIFF.API/SARIFF.API.csproj", "src/SARIFF.API/"]
RUN dotnet restore "src/SARIFF.API/SARIFF.API.csproj"

COPY . .
RUN dotnet publish "src/SARIFF.API/SARIFF.API.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "SARIFF.API.dll"]
