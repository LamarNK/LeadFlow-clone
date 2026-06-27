FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/NotifyBot.Domain/ src/NotifyBot.Domain/
COPY src/NotifyBot.Application/ src/NotifyBot.Application/
COPY src/NotifyBot.Infrastructure/ src/NotifyBot.Infrastructure/
COPY src/NotifyBot.Api/ src/NotifyBot.Api/
RUN dotnet restore src/NotifyBot.Api/NotifyBot.Api.csproj
RUN dotnet publish src/NotifyBot.Api/NotifyBot.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "NotifyBot.Api.dll"]