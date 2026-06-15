FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["SalaryTelegramBot.Api/SalaryTelegramBot.Api.csproj", "SalaryTelegramBot.Api/"]
RUN dotnet restore "SalaryTelegramBot.Api/SalaryTelegramBot.Api.csproj"

COPY . .
WORKDIR "/src/SalaryTelegramBot.Api"
RUN dotnet publish "SalaryTelegramBot.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS="http://+:8080"

EXPOSE 8080

ENTRYPOINT ["dotnet", "SalaryTelegramBot.Api.dll"]
