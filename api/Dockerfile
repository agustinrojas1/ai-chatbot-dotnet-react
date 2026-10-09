FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Chatbot.slnx ./
COPY src/Chatbot.Api/Chatbot.Api.csproj src/Chatbot.Api/
COPY src/Chatbot.Core/Chatbot.Core.csproj src/Chatbot.Core/
COPY src/Chatbot.Infrastructure/Chatbot.Infrastructure.csproj src/Chatbot.Infrastructure/
RUN dotnet restore src/Chatbot.Api/Chatbot.Api.csproj
COPY src/ src/
RUN dotnet publish src/Chatbot.Api/Chatbot.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Chatbot.Api.dll"]
