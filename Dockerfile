FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Blaster.Batch/Blaster.Batch.csproj src/Blaster.Batch/
COPY src/Blaster.Valve/Blaster.Valve.csproj src/Blaster.Valve/
COPY src/Blaster.CLI/Blaster.CLI.csproj src/Blaster.CLI/

RUN dotnet restore src/Blaster.CLI/Blaster.CLI.csproj

COPY . .
RUN dotnet publish src/Blaster.CLI/Blaster.CLI.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "Blaster.CLI.dll"]
