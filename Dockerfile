# 1. Etapa de compilación
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiar el archivo de solución
COPY ["UnaPlanProject.sln", "./"]

# Copiar los archivos de proyecto (.csproj) recreando sus carpetas
COPY ["UnaPlan.Api/UnaPlan.Api.csproj", "UnaPlan.Api/"]
COPY ["UnaPlan.Core/UnaPlan.Core.csproj", "UnaPlan.Core/"]
COPY ["UnaPlan.Infrastructure/UnaPlan.Infrastructure.csproj", "UnaPlan.Infrastructure/"]

# Restaurar todas las dependencias
RUN dotnet restore "UnaPlanProject.sln"

# Copiar el resto del código de todas las carpetas
COPY . .

# Publicar solo el proyecto de la API
WORKDIR "/src/UnaPlan.Api"
RUN dotnet publish "UnaPlan.Api.csproj" -c Release -o /app/publish

# 2. Etapa final (Runtime)
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .

# Configurar el puerto para Render
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000

# IMPORTANTE: Verifica que el nombre de la DLL sea UnaPlan.Api.dll
ENTRYPOINT ["dotnet", "UnaPlan.Api.dll"]
