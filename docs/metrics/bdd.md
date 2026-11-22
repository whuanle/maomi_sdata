# metrics — BDD

> 关联：sdd（T900 补齐） · tdd（T900 补齐） · sop（T900 补齐）

Feature: 指标查询

  @METRICS-GO1 @auto:e2e
  Scenario: Go 兼容查询返回真实指标而非固定空结果
    Given 登录用户已摄取两个时间点的计数器样本
    When 执行选择器、标签聚合、窗口速率和仪表盘预览
    Then 返回原协议序列与正确数值且面板保留毫秒时间精度

  @METRICS-SEARCH1 @auto:xunit
  Scenario: 统一查询 DSL 分桶聚合（POST /api/metrics/search）
    When 提交 range + bucket.step=1h avg(value) 与 labels.host 标签过滤
    Then 返回 bucket/avg 列式结果，json_extract_string(labels,...) 提取标签

  @METRICS-SEARCH2 @auto:xunit
  Scenario: 区间内无数据表短路
    When 查询远早于建库的区间
    Then 返回空行、列名为 metrics 物理列、TablesScanned=0

  @METRICS-PROM1 @auto:xunit
  Scenario: PromQL rate 端到端（Prometheus 兼容）
    Given counter 三样本 (100,200,400) 跨 300 秒写入 metrics 天表
    When GET /api/v1/query?query=rate(http_server_requests_total[5m])
    Then 每序列返回 (max-min)/300s ≈ 1.0/s 的 vector 结果，metric 标签含 __name__ 与 service

  @METRICS-PROM2 @auto:xunit
  Scenario: PromQL 聚合与 topk
    When 执行 sum by (service) (cpu_usage) 与 topk(2, mem_used)
    Then sum 按 service 标签分组合并为每服务单序列；topk 按值降序截断前 2

  @METRICS-PROM3 @auto:xunit
  Scenario: query_range matrix 与错误表达式
    When GET /api/v1/query_range（step=10s）查询含三个 host 序列的表达式
    Then 返回 matrix（每序列独立 values 数组，单点数据仅落一个桶）
    When 表达式非法（如 "sum("）
    Then 返回 status=error、errorType=bad_data、中文错误消息（Prometheus 形状）

  @METRICS-S1 @manual
  Scenario: 指标曲线统一展示
    Given 查询返回多组标签的指标序列
    When 查看指标曲线
    Then 使用统一图例与提示框，缺失点保留缺口并提供时间缩放
