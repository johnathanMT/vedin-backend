# ═══════════════════════════════════════════════════════════════
#  Dockerfile — PortfolioApi (.NET 8)
#  Multi-stage build: minimises final image size (~120 MB)
#  Optimised for Render's container deployment
# ═══════════════════════════════════════════════════════════════

# ── Stage 1: Build ─────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy only the project file first to leverage Docker layer cache.
# The restore layer is rebuilt only when .csproj changes.
COPY ["PortfolioApi.csproj", "."]
RUN dotnet restore "./PortfolioApi.csproj" --runtime linux-x64

# Copy the rest of the source code
COPY . .

# Publish — self-contained, single-file, trimmed, Release mode
RUN dotnet publish "./PortfolioApi.csproj" \
    -c Release \
    -r linux-x64 \
    --no-restore \
    --self-contained false \
    -o /app/publish

# ── Stage 2: Runtime ───────────────────────────────────────────
# Use the minimal ASP.NET runtime image (not the SDK)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# QuestPDF renders through SkiaSharp/HarfBuzz and needs BOTH the native font stack
# and real font files. The Myanmar font packages are NOT reliably available via apt on
# this base image (the build failed with "Unable to locate package fonts-padauk"), so
# we install the native libs + a Latin face (DejaVu, always in the main repo) via apt,
# and fetch the Burmese fonts (Padauk + Noto Sans Myanmar) as TTFs directly. These match
# the exact family names VedinTheme.cs requests ("Padauk", "Noto Sans Myanmar",
# "DejaVu Sans"). Without them Burmese silently renders as tofu boxes.
# wget/ca-certificates also satisfy the HEALTHCHECK below.
RUN apt-get update && apt-get install -y --no-install-recommends \
        fontconfig \
        libfontconfig1 \
        fonts-dejavu-core \
        wget \
        ca-certificates \
    && mkdir -p /usr/share/fonts/truetype/vedin \
    # Padauk (primary Burmese face) — required.
    && wget -q -O /usr/share/fonts/truetype/vedin/Padauk-Regular.ttf \
         https://github.com/google/fonts/raw/main/ofl/padauk/Padauk-Regular.ttf \
    && wget -q -O /usr/share/fonts/truetype/vedin/Padauk-Bold.ttf \
         https://github.com/google/fonts/raw/main/ofl/padauk/Padauk-Bold.ttf \
    # Noto Sans Myanmar (fallback) — best-effort: if the fetch fails, drop the partial
    # file and carry on; Padauk already covers Burmese, so the build must not break.
    && ( wget -q -O /usr/share/fonts/truetype/vedin/NotoSansMyanmar-Regular.ttf \
           https://raw.githubusercontent.com/notofonts/notofonts.github.io/main/fonts/NotoSansMyanmar/hinted/ttf/NotoSansMyanmar-Regular.ttf \
         || rm -f /usr/share/fonts/truetype/vedin/NotoSansMyanmar-Regular.ttf ) \
    && fc-cache -f \
    && rm -rf /var/lib/apt/lists/*

# Security: run as a non-root user
RUN addgroup --system --gid 1001 appgroup && \
    adduser  --system --uid 1001 --ingroup appgroup --no-create-home appuser

# Copy published output from build stage
COPY --from=build /app/publish .

# Render sets PORT env variable dynamically; fall back to 8080
ENV ASPNETCORE_URLS="http://+:${PORT:-8080}"
ENV ASPNETCORE_ENVIRONMENT="Production"
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Switch to non-root user before starting
USER appuser

# Expose the port (Render reads PORT env var, but this documents intent)
EXPOSE 8080

# Health-check so Render knows the container is alive
HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
    CMD wget -qO- http://localhost:${PORT:-8080}/health || exit 1

ENTRYPOINT ["dotnet", "PortfolioApi.dll"]
