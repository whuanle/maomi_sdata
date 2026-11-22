# store — BDD

> 关联：[sdd](sdd.md) · [tdd](tdd.md) · [sop](sop.md)

Feature: DuckDB 观测数据存储

  @STORE-PF04 @auto:xunit
  Scenario: Rollup 仅重算前进水位附近固定迟到窗口内的桶
    Given 首轮回填已将水位写入 DuckDB，随后 DuckDB 连接管理器重启且水位前固定迟到窗口（10 分钟）内的桶又收到迟到指标
    When 再次执行 5m 与 1h 回填
    Then 迟到窗口内的桶重新聚合，窗口外的旧桶不被扫描；水位与最后桶替换在同一事务提交

  @STORE-PF04 @auto:xunit
  Scenario: 水位写入失败后事务回滚并在重启后恢复
    Given 最后一个 rollup 桶已有汇总，水位表约束会拒绝新的水位
    When 回填替换该桶并尝试推进水位失败，随后重启 DuckDB 连接并移除故障后重试
    Then 故障后重启仍保留原汇总和水位；重试更新迟到数据并推进水位

  @STORE-PF07 @auto:xunit
  Scenario: 记录源切换独占锁的持有时间
    Given 源切换操作已获取独占租约
    When 操作释放租约
    Then `sdata_store_source_switch_lock_hold_seconds` 直方图记录从获取至释放的时间，且与锁等待时间分开

  @STORE-PF09 @auto:xunit
  Scenario: MQ 一批日志只等待一个持久化回执
    Given 一批日志通过有界缓冲等待写入器
    When 缓冲接收完整批次且写入器提交事务
    Then 仅批次最后一条携带回执，回执在事务提交后完成，有等待者时跳过低水位等待

  @STORE-CACHE1 @auto:xunit
  Scenario: 查询结果短 TTL 缓存命中与桶边界失效
    Given DSL 统一查询开启结果缓存（QueryCacheTtlSeconds > 0）
    When 同一签名查询在 TTL 时间桶内重复提交，随后跨过桶边界再次提交
    Then 桶内命中缓存直接返回相同响应，跨桶后重新执行并刷新缓存
    Then 响应行数超过缓存上限（1000 行）不缓存；TTL 为 0 时全部请求直通内层执行器

  @STORE-S39 @auto:xunit
  Scenario: DSL 查询可选列式响应
    Given 查询请求 columnar 为 true
    When 执行统一查询
    Then 响应 rows 为空、rowsArrays 与 columns 对齐且 columnar 标记为 true
    Then 缺省请求仍返回键值行形式，行为与既有契约一致

  @STORE-S36 @auto:xunit
  Scenario: 持续回填和重启不重复放大汇总存储
    Given 同一批指标反复参与汇总
    When 连续回填并重新打开数据库
    Then 汇总数值幂等且存储增长受控，旧版写放大索引在引导时移除

  @STORE-S37 @auto:xunit
  Scenario: 汇总替换失败保留原结果且过期汇总被清理
    Given 汇总桶已有完整结果且部分桶超过指标总保留期
    When 新计算失败或取消并执行保留清理
    Then 原桶不被提前删除，只有过期桶被清理且保留期边界仍可查询

  @STORE-S38 @auto:xunit
  Scenario: 固定内存下接入查询聚合与整理混合运行
    Given 实例使用固定内存预算
    When 持续交错接入、宽结果查询、汇总和存储整理
    Then 日志完整提交且查询名额释放，数据库持续可用而不依赖重启


  @STORE-MEM1 @auto:xunit
  Scenario: 调整内存限制共享到所有查询连接
    Given 数据库已有打开的查询连接
    When 管理员调大内存限制或调整为较小值
    Then 已有及新连接均读取新的实例限制，线程配置不变且非法容量被拒绝

  @STORE-MEM2 @auto:xunit
  Scenario: 重启恢复管理员保存的内存限制
    Given 管理员已保存不同于文件默认的内存限制
    When 服务重新初始化数据库
    Then 引导存储结构前应用保存值且文件默认保持不变

  @STORE-S31 @auto:xunit
  Scenario: 查询资源受限且不阻塞批量接入
    Given 小内存实例已达到查询并发上限
    When 新查询等待准入或取消且日志进入缓冲
    Then 等待查询不执行且取消释放等待，日志仍可提交，结果读取期间保留查询名额

  @STORE-S32 @auto:xunit
  Scenario: 批量事务与后台存储写入互斥
    Given 存储写入门正在使用
    When 日志批次或存储整理等待写入且取消
    Then 不开始并行写事务且释放等待的表锁，后续批次可正常提交

  @STORE-S33 @auto:xunit
  Scenario: 内存失败重试不提前确认消息
    Given 某日志批次在提交前发生临时失败
    When 故障消除且原批次重试成功
    Then 失败时无部分落库与成功回执，重试保留记录标识且提交后才完成回执

  @STORE-S34 @auto:xunit
  Scenario: 日志列布局缓存随分区生命周期更新
    Given 日志分区已经缓存实际列序
    When 分区冻结或删除后重建
    Then 丢弃旧缓存并按新分区实际列序继续追加

  @STORE-S35 @auto:xunit
  Scenario: 存储资源故障安全分类
    Given 存储出现内存不足、数据库失效或读写故障
    When 查看共享写入器故障快照
    Then 返回对应安全原因且不含异常原文，数据库失效明确提示检查日志并重启


  @STORE-AGENT1 @auto:xunit
  Scenario: 只读分析统一读取浏览器事件冷热分区
    Given 浏览器事件和其他观测数据跨日保存在冷热层
    When 受限分析按逻辑数据源和项目查询
    Then 参数安全绑定且合并冷热结果，不暴露回放或内部存储位置

  @STORE-MQ5 @auto:xunit
  Scenario: 消费通知冷却与时序统计分开保存
    Given 消费通知已预留冷却窗口
    When 服务重新加载配置和通知状态
    Then 冷却元数据保留在元数据库且不因消费会话重建清零，消费统计仍按原时序存储查询

  @STORE-MQ4 @auto:xunit
  Scenario: 消费诊断兼容旧采样表并安全报告写入故障
    Given 消费采样表尚无诊断列且批量写入可能失败
    When 重复执行增量补列并读取故障快照
    Then 诊断可往返读取，故障原因不含异常原文且恢复时间与失败时间分开保留

  @STORE-S30 @auto:xunit
  Scenario: 告警查询受限且可取消
    Given 告警只评估近期热数据
    When 查询达到计数上限或执行超时
    Then 返回明确的计数下界或评估失败且释放执行资源

  @STORE-MQ3 @auto:xunit
  Scenario: 消费采样跨日保留独立会话
    Given 同一实例在不同日期和消费会话产生累计采样
    When 按实例与时间范围读取统计
    Then 返回对应采样且保留解析失败和采样缺口，不将不同会话累计求和

  @STORE-S9 @manual
  Scenario: 开发热更新忽略运行时数据
    Given 开发环境已启用代码热更新
    When 数据目录内的数据库或日志文件变化
    Then 不触发热更新且代码仍被监听，应用配置的默认行为不变

  @STORE-RES1 @auto:xunit
  Scenario: 存储资源采样不修改业务数据
    Given 数据库已打开
    When 后台采集资源用量
    Then 返回实际追踪内存、临时溢出、线程配置、数据库文件和数据卷容量且不触发写入

  @STORE-RES2 @auto:xunit
  Scenario: 对象存储统计只发布完整结果
    Given 对象存储需要多页列举
    When 后台统计配置范围的对象
    Then 仅完整成功后发布总字节数和对象数，失败或超时不发布部分总量

  @STORE-RES3 @auto:xunit
  Scenario: 对象存储配置切换隔离旧数据
    Given 旧目标已完成统计
    When 切换存储桶、前缀或凭据
    Then 立即清除旧目标的展示值并重新采样且不暴露原始异常或凭据

  @STORE-S8 @auto:xunit
  Scenario: 客户端会话支持只读分析
    Given 查询时间范围内存在客户端会话存储
    When 按项目条件执行会话统计
    Then 项目条件安全绑定且查询返回对应统计

  @STORE-MQ1 @auto:xunit
  Scenario: 日志落库完成回执与并发背压
    Given 多个生产者同时向有界缓冲写入
    When 缓冲已满或日志尚未落库
    Then 满载写入不会被误报成功，持久化回执仅在事务提交后完成

  @STORE-MQ2 @auto:xunit
  Scenario: 旧日志表升级后继续追加
    Given 旧日志表已补齐来源和记录标识
    When 新日志批量写入该表
    Then 字段按实际列序正确落库并可查询

  @STORE-S1 @auto:xunit
  Scenario: 日志写入后可按服务与关键字查询
    Given 服务已启动且 DuckDB 建库完成
    When 接入一条 svc-a 的 ERROR 日志 body 含 "boom"
    Then 按服务 svc-a 与关键字 boom 查询应返回该日志

  @STORE-S1 @auto:xunit
  Scenario: 日志查询分页下推到 SQL
    Given 同一服务在近一分钟内写入多条按时间倒序的日志
    When 以 Limit=2 分别请求 Offset=0 与 Offset=2
    Then 第一页返回最新两条，第二页返回之后两条，不发生内存跳过导致空页

  @STORE-S2 @auto:xunit
  Scenario: 链路写入后可还原完整 trace 与统计
    When 写入同一 trace 的两个 span（其一状态为 error）
    Then 按 traceId 查询应返回 2 个 span
    Then trace 列表统计应显示 span 数 2、错误数 1

  @STORE-S3 @auto:xunit
  Scenario: 指标按步长聚合
    When 5 秒内写入 5 个 cpu_usage 数据点
    Then 按 60s 步长 avg 聚合应得到非空序列且均值正确

  @STORE-S4 @auto:xunit
  Scenario: 遥测最新值与聚合
    When 设备 dev-1 先后上报温度 20 与 22
    Then 最新值查询应返回 22
    Then 区间聚合应返回 temperature 序列

  @STORE-S5 @auto:xunit
  Scenario: 分析执行器拒绝写操作
    When 提交 DELETE 语句或多语句
    Then 执行应被拒绝

  @STORE-S6 @manual
  Scenario: 保留策略过期表清理
    Given 保留天数为 N
    When 存在早于 N 天的天表
    Then 该表被 DROP 且磁盘空间经 CHECKPOINT 释放

  @STORE-S7 @manual
  Scenario: 查询不存在的日期分表返回业务错误
    When 提交 SELECT 引用尚未创建的天表（如未来日期 logs_20270101）
    Then 返回 400 业务错误「查询的数据表不存在…」，而非 HTTP 500 裸异常

  @STORE-DSL1 @auto:xunit
  Scenario: 四域 DSL 编译为参数化 SQL（契约 C1）
    When 对 logs/traces/metrics/iot 各提交含 filters/sort/limit 的查询模型
    Then 编译产物为 SELECT 且一切外部值经 DuckDBParameter 绑定、表名来自 TimeTables 天表集合
    Then 白名单外字段/操作符/聚合函数拒绝并抛 400 BusinessException

  @STORE-DSL2 @auto:xunit
  Scenario: JSON 标签过滤走 json_extract_string
    When 过滤 attributes.k8s.pod（logs/traces）或 labels.host（metrics）
    Then 编译为 json_extract_string 列提取且路径非法字符被拒绝

  @STORE-DSL3 @auto:xunit
  Scenario: 时间分桶直方图与聚合
    When 提交 bucket.step=5m 与 groupBy/aggregations（count/avg/sum/min/max/quantile）
    Then 编译为 time_bucket 分组并输出 bucket/别名列，排序仅允许输出别名

  @STORE-DSL4 @auto:xunit
  Scenario: DSL 聚合查询在真实 DuckDB 执行
    Given 落库三条日志（svc1×2、svc2×1）与两个同小时指标点
    When 按 groupBy service count 与 bucket 1h avg value 分别执行
    Then 返回 svc1=2/svc2=1 与单桶均值 15

  @STORE-DSL5 @auto:xunit
  Scenario: 比较操作符（gt/gte/lt/lte）
    When 过滤 duration_us > 100000（数值）、attributes.code >= 400（JSON）、ts < ISO 时间（时间）
    Then 数值/时间字段直接比较且参数绑定，JSON 字段经 TRY_CAST(… AS DOUBLE) 数值比较
    Then 字符串字段比较拒绝并抛 400（统一搜索页 HyperDX 式 field:>100 语法走此通道）

Feature: 冷存档与三段保留（T512，契约 C4/C5）

  @STORE-ARCH7 @auto:xunit
  Scenario: 一天热存期归档上一完整日期的数据
    Given 日志启用冷存储且本地保留一天，昨天与今天均有数据
    When 后台执行自动归档
    Then 昨天的数据转入冷存储，今天的数据仍留在热层，重复执行不重复归档

  @STORE-ARCH8 @auto:xunit
  Scenario: 对象存储目标不作为本地目录处理
    Given 归档目标为对象存储地址
    When 准备归档目标
    Then 跳过本地文件删除和目录创建

  @STORE-ARCH1 @auto:xunit
  Scenario: 归档幂等（COPY→行数校验→台账→DROP）
    Given 超过热存期的历史天表（2 行）且未配置 S3（写本地冷存目录）
    When ArchiveService 单轮归档，随后再跑一轮
    Then 第一轮后台账（domain/day/objectKey/rowCount=2）、对象落盘、天表 DROP；第二轮零归档

  @STORE-ARCH2 @auto:xunit
  Scenario: 三段保留：归档即转冷、到期清理对象
    Given cold=30d：today-40d 与 today-10d 两个天表均先归档（归档即 DROP 热表，数据在冷层 parquet）
    When RetentionService 单轮清理
    Then 超过 hot+cold 的 today-40d 冷对象与台账行被清理；today-10d 在冷窗口内对象与台账存活

  @STORE-PF07 @auto:xunit
  Scenario: 过期冷对象删除不占用查询交接锁
    Given 单个已归档对象超过热加冷总保留期，且对象删除被故障注入阻塞
    When 保留任务清理过期对象并收到查询
    Then 查询不等待删除网络请求；删除前对象从候选目录隐藏，删除失败后恢复台账和可查询性，下一轮可重试清理

  @STORE-PF05 @auto:xunit
  Scenario: Rollup、归档与保留串行共享维护门
    Given 维护任务持有并发上限为 1 的维护门
    When 两个维护任务同时进入且等待一个写入任务完成
    Then 第二个维护任务等待、取消可释放许可、摄取不因维护门而阻塞且维护等待计入可观测指标

  @STORE-PF06 @auto:xunit
  Scenario: 冻结批次 COPY 与词项索引不占写门
    Given 历史天表已冻结且写门被另一个目录切换持有
    When 归档执行 COPY 与词项索引构建
    Then 两项由克隆只读连接完成，冻结批次对象落盘且写门持有者不等待网络或聚合操作

  @STORE-ARCH3 @auto:xunit
  Scenario: 旧形态兼容（ColdDays=0 直接过期）
    Given HotDays=0 回退旧形态 DuckDb.Retention.Logs=5 且 ColdDays=0
    When 跑归档与清理
    Then today-10d 被直接 DROP、台账为空（与旧版行为一致）

  @STORE-ARCH4 @auto:xunit
  Scenario: 归档增强：行级 id、排序写、词项索引镜像
    Given 经写入器落库 3 行日志（services zeta/alpha/mike 乱序，正文含中文与英文 token，行级 id 由写入器赋值）
    When ArchiveService 单轮归档
    Then parquet 行含非空 id 且按 service 升序；data/cold-idx/logs/{day}.idx.parquet 存在且含 '超时' 二元组与 'ord42' token

  @STORE-ARCH5 @auto:xunit
  Scenario: 小时分片粒度（ArchiveGranularity=hour）
    Given 3 行日志跨小时 08/09，S3 设置 ArchiveGranularity=hour
    When ArchiveService 单轮归档
    Then 生成 2020110108/09 两个对象与对应索引镜像（分片内按 service 排序），其余 22 个空分片跳过；台账 (domain,day) 两行

  @STORE-COLD1 @auto:xunit
  Scenario: 台账驱动冷查询与本地冷目录兜底
    Given 台账 2 天（1 行 rowCount=0）或本地 data/cold 目录有归档对象
    When Resolve 查询区间
    Then 台账非空输出逐日精确路径（rowCount=0 排除，Glob 兜底互斥）；台账为空且无 S3 时输出本地目录通配

  @STORE-COLD2 @auto:xunit
  Scenario: 冷缓存命中与 LRU 淘汰
    Given 本地冷存对象 A/B 与 ColdCacheService（容量后压到 B 大小）
    When Warm A 命中后 Warm B
    Then A/B 均落 data/cache/cold 镜像；容量超限后最久未访问的 A 被淘汰、B 存活；Invalidate 删除缓存

  @STORE-COLD3 @auto:xunit
  Scenario: 关键词候选分片裁剪（词项索引）
    Given 两天台账、仅 day1 有索引镜像（含 '超时'）
    When 分别以命中词、未命中词、无关键词 Resolve
    Then 命中=day1+day2（无镜像保守保留）；未命中=仅 day2（day1 被裁）；无关键词=全部保留

  @STORE-COLD4 @auto:xunit
  Scenario: S3 兜底通配验存（空桶不连累本地查询）
    Given 已配置 S3 且归档台账为空（桶内尚无该域对象，或端点不可达/鉴权失败/桶不存在）
    When Resolve 查询区间
    Then 先以 glob() 验存：零命中或验存异常一律跳过冷层分支（fail-open，Debug/Warning 日志），查询仅走本地天表；
         验存命中才输出域级通配（与 read_parquet 同引擎同 SECRET，验存视角与后续查询一致）；
         read_parquet 对零命中通配会在语句 prepare 阶段抛 IO Error（No files found）把本地可查的搜索打成 500，此为验存存在的原因

  @STORE-COLD6 @auto:xunit
  Scenario: 验存探测窗口缓存与熔断（S3 故障不拖慢搜索）
    Given 探测发生在查询请求路径上（S3 故障时一次探测可能拖住整次查询）
    When 首次验存完成后按配置指纹+域缓存结果：命中 60s / 零命中 30s / 异常熔断 5min
    Then 窗口内后续 Resolve 不再触碰 S3：命中直接带通配、零命中/熔断直接跳过冷层，且不再重复告警；
         配置变更（端点/桶/前缀）自动换键立即重新探测；窗口过后自动重试恢复

  @STORE-COLD5 @auto:xunit
  Scenario: 冷启动重建设置页持久化 S3 SECRET
    Given 设置页保存过 S3 配置（SQLite setting 表 system:s3）且文件配置 SData:S3 为空，服务重启
    When StoreInitializer.StartAsync（晚于 DatabaseInitializer，SQLite 已就绪）
    Then 以 GetS3()（覆盖 &gt; 文件回退）为真相源 CREATE OR REPLACE SECRET sdata_s3（幂等），
         并把合并值同步回 SystemOptions.S3（S3ObjectStore/ColdCacheService 等读 _options.S3 的通道同源生效）；
         未配置 S3 时不建 SECRET（零行为变化）。
         缺此步骤时查询侧拼出 s3:// URL 而连接上无匹配 SECRET，httpfs 走默认 AWS 端点（报 in region '' + NoSuchBucket，
         请求根本没到自建 MinIO——连接测试正常因为它用表单值临时建 SECRET）

  @STORE-S3SEC @auto:xunit
  Scenario: S3 SECRET 端点归一化与 path-style
    Given 端点分别填 http://minio:9000、minio:9000（含/不含协议头）与结尾斜杠
    Then 均离线建 SECRET 成功且 ENDPOINT 为裸 host[:port]、URL_STYLE 'path'（IP 端点虚主机寻址无法解析）；仅填 http:// 视为未配置不建 SECRET

  @STORE-ARCH6 @manual
  Scenario: S3 冷对象超期清理（三段保留终点）
    Given MinIO 环境（configs/minio-compose.yml）与超过 hot+cold 的归档对象
    When RetentionService 单轮清理
    Then S3 对象、索引对象、本地镜像与台账行均被清理；删除失败保留台账下轮重试

  @STORE-ARCH9 @auto:xunit
  Scenario: 四类数据归档后仍可查询
    Given 日志、指标、链路及会话关联数据已超过本地保留期
    When 自动归档完成
    Then 历史查询、会话事件与回放结果保持一致，项目之间互相隔离

  @STORE-ARCH10 @auto:xunit
  Scenario: 迟到数据与失败批次可恢复
    Given 历史数据已归档或某批次上传失败
    When 收到同日迟到数据并重试归档
    Then 历史对象不被覆盖，失败批次保留且补报数据可查询

  @STORE-ARCH11 @auto:xunit
  Scenario: 已发布批次重启后不重复计数
    Given 归档台账已发布但本地副本尚未清理
    When 服务重新启动并恢复归档
    Then 查询仅计数一次且恢复不重复发布对象
