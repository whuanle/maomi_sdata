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
# 指定 RID：只带 linux-x64 的 DuckDB 原生绑定（全平台绑定约 350MB → 50MB）
RUN dotnet publish src/SData/SData.csproj -c Release -r linux-x64 --self-contained false -o /app/publish --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=backend-build /app/publish ./
COPY --from=ui-build /src/ui/dist-output ./wwwroot
# 默认参数内置于 appsettings.json；首次启动复制为 /app/configs/appsettings.json 供覆盖，/app/configs 另存运行时生成的 RSA 私钥
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5300
VOLUME ["/app/data", "/app/configs"]
ENTRYPOINT ["dotnet", "SData.dll"]
