FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["OWSDatabaseMigrations/OWSDatabaseMigrations.csproj", "OWSDatabaseMigrations/"]
RUN dotnet restore "OWSDatabaseMigrations/OWSDatabaseMigrations.csproj"
COPY . .
WORKDIR "/src/OWSDatabaseMigrations"
RUN dotnet publish "OWSDatabaseMigrations.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# The compose file mounts Databases/Postgres (which holds migration-manifest.txt) at /migrations.
ENTRYPOINT ["dotnet", "OWSDatabaseMigrations.dll", "--scripts", "/migrations"]
