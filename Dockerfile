FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet restore TransferProof.slnx
RUN dotnet publish src/TransferProof.Api/TransferProof.Api.csproj --no-restore -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
USER root
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg tesseract-ocr tesseract-ocr-eng libfontconfig1 fonts-dejavu-core && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p appdata storage && chown -R $APP_UID:$APP_UID appdata storage
ENV ASPNETCORE_URLS=http://+:5101
EXPOSE 5101
USER $APP_UID
ENTRYPOINT ["dotnet", "TransferProof.Api.dll"]
