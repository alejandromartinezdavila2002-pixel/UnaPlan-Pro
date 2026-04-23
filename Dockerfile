# 1. Etapa de compilación
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiar la solución principal
COPY ["UnaPlanProject.sln", "./"]

# Copiar las tres capas del proyecto respetando sus carpetas
COPY ["UnaPlan.Api/UnaPlan.Api.csproj", "UnaPlan.Api/"]
COPY ["UnaPlan.Core/UnaPlan.Core.csproj", "UnaPlan.Core/"]
COPY ["UnaPlan.Infrastructure/UnaPlan.Infrastructure.csproj", "UnaPlan.Infrastructure/"]

# Restaurar paquetes de Nuget
RUN dotnet restore "UnaPlanProject.sln"

# Copiar absolutamente todo el resto del código
COPY . .

# Compilar y publicar la API
WORKDIR "/src/UnaPlan.Api"
RUN dotnet publish "UnaPlan.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# 2. Etapa de ejecución (Runtime)
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .

# Configurar el puerto para Render
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000

# Encender el motor (Tu DLL se llama UnaPlan.Api.dll basado en tu .csproj)
ENTRYPOINT ["dotnet", "UnaPlan.Api.dll"]
