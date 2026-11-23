# SData — 内网单机部署镜像（前端内嵌 wwwroot，同源单容器）
FROM node:22-alpine AS ui-build
WORKDIR /src/ui
# 本项目是 pnpm workspace（pnpm-lock.yaml/pnpm-workspace.yaml），npm 无法解析
# 先复制清单文件以利用层缓存
COPY ui/package.json ui/pnpm-lock.yaml ui/pnpm-workspace.yaml ./
COPY ui/packages/ ./packages/
RUN npm i -g pnpm@10 --no-audit --no-fund && pnpm install --frozen-lockfile
COPY ui/ ./
# 部分前端测试通过 ../../../../../tests/... 引用后端真实数据库 fixture（前后端 SQL 契约测试），
# typecheck 需要这些 JSON 存在于构建上下文对应路径；仅进入中间阶段，不进最终镜像。
COPY tests/SData.Store.Tests/Fixtures/ /src/tests/SData.Store.Tests/Fixtures/
# 输出到独立目录，最终阶段复制进宿主 wwwroot
RUN pnpm typecheck \
    && pnpm exec vite build --outDir dist-output \
    && pnpm exec vite build --config vite.probe.config.ts --outDir dist-output/rum

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src
# RepoRoot 由 .gitignore 位置推导，必须一并复制
COPY .gitignore Directory.Build.props Directory.Packages.props SData.slnx ./
COPY src/ src/
# 自包含单文件发布（与 CI Release 同参数）：运行层不再依赖 aspnet 共享运行时。
# IncludeNativeLibrariesForSelfExtract=false → 原生库（libduckdb 等）松散随 apphost 放置，
# 与旧框架依赖发布同布局，启动零自解压开销；PublishTrimmed/PublishReadyToRun 必须保持关闭
# （Maomi 模块反射与 MCP 的 System.Text.Json 反射不兼容裁剪，R2R 只增体积）。
RUN dotnet publish src/SData/SData.csproj -c Release -r linux-x64 --self-contained true \
    -p:UseAppHost=true -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=false \
    -p:EnableCompressionInSingleFile=true \
    -p:PublishReadyToRun=false -p:PublishTrimmed=false \
    -p:DebugType=None -p:DebugSymbols=false \
    -o /app/publish --nologo

# 运行层用基础 Linux（glibc 系，musl/Alpine 不支持——DuckDB 等原生库仅出 glibc 绑定），
# 自带运行依赖：TLS 证书（S3 https 端点）、zlib/libstdc++（DuckDB/Kafka 原生库）、时区数据、
# wget（docker-compose healthcheck 使用）。
FROM debian:bookworm-slim AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates libssl3 zlib1g libstdc++6 tzdata wget \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=backend-build /app/publish ./
COPY --from=ui-build /src/ui/dist-output ./wwwroot
# 默认参数内置于 appsettings.json；首次启动复制为 /app/configs/appsettings.json 供覆盖，/app/configs 另存运行时生成的 RSA 私钥
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
# 全球化运行于 invariant 模式（免装 libicu，约省 40MB）：业务代码全部 InvariantCulture、无时区查询用法；
# 如需恢复完整 ICU 全球化：上方 apt 追加 libicu72 并删除该环境变量。
EXPOSE 5300
VOLUME ["/app/data", "/app/configs"]
ENTRYPOINT ["./SData"]
