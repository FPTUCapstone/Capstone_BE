FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY TripMate.slnx ./
COPY src/TripMate.Domain/TripMate.Domain.csproj src/TripMate.Domain/
COPY src/TripMate.Application/TripMate.Application.csproj src/TripMate.Application/
COPY src/TripMate.Infrastructure/TripMate.Infrastructure.csproj src/TripMate.Infrastructure/
COPY src/TripMate.Api/TripMate.Api.csproj src/TripMate.Api/
RUN dotnet restore src/TripMate.Api/TripMate.Api.csproj

COPY src/ src/
RUN dotnet publish src/TripMate.Api/TripMate.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "TripMate.Api.dll"]
