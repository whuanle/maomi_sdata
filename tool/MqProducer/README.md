# MQ 日志闭环验收与运维

关联：[ingest SOP](../../docs/ingest/sop.md) · [settings](../../docs/settings/sop.md) · [monitor](../../docs/monitor/sop.md) · [验收脚本](verify.py)

## 隔离测试环境

- 仅对已授权的 Kafka、RabbitMQ、Pulsar 与 SData 测试实例运行，不使用生产主题或队列。
- 部署地址、SSH 账号、服务器路径与凭据不记录到仓库，由本机环境变量或密钥服务提供。
- Kafka 需要时启用 SASL/SCRAM；RabbitMQ 的 `RABBIT_URI` 必须由环境变量提供，程序没有内置用户名或密码。
- 测试 broker 不应暴露公网；配置认证/TLS，限制主题保留、队列长度与消息 TTL，避免持续造数耗尽磁盘。
- 在 SData 配置三类消费者及 JSON 解码规则。精确规则 `sdata-e2e-exact` 优先，前缀 `sdata-e2e-` 次之；Pulsar 主题加 `persistent://public/default/`。RabbitMQ 前缀匹配队列名，不是 exchange routing key。

## 造数程序

```bash
dotnet publish tool/MqProducer/MqProducer.csproj -c Release -r linux-x64 --self-contained false -o local-dev/mq-producer
```

环境变量：

| 变量 | 用途 |
|---|---|
| `MQ_HOST` | Kafka/Pulsar 主机，默认 localhost |
| `TARGET` | kafka / rabbitmq / pulsar / all，默认 all |
| `RUN_ID` | 本次运行标识，默认随机生成 |
| `TOPIC_SUFFIX` | 目标 `sdata-e2e-{suffix}`，默认 exact |
| `COUNT` | 每类消息数，0 表示持续，默认 0 |
| `INTERVAL_MS` | 消息间隔，默认 1000，最低 100 |
| `KAFKA_SECURITY_PROTOCOL` / `KAFKA_SASL_MECHANISM` | Kafka 认证协议与机制 |
| `KAFKA_SASL_USERNAME` / `KAFKA_SASL_PASSWORD` | Kafka 凭据，仅从环境读取 |
| `RABBIT_URI` | RabbitMQ 连接信息，仅从环境读取，选择 rabbitmq/all 时必填 |

有限批次建议 `COUNT=30 INTERVAL_MS=100`。每条日志含 runId、序号、时间、服务、级别和正文；连接失败 5 秒重试，确认后打印序号。不要将真实凭据直接写入命令历史或共享日志。

## 验收与停启

1. 配置 `SDATA_API_BASE`（测试实例 API 根地址，以 `/api` 结尾）、`SDATA_USERNAME`（默认 admin）与 `SDATA_PASSWORD` 环境变量。
2. 生成 exact 与 prefix-new 两批主题消息，再执行 `python3 -I tool/MqProducer/verify.py`。
3. 脚本拒绝重定向，仅访问登录、分析查询和监控状态端点；不直接打开运行中的 DuckDB。
4. 验证三类 broker 的精确与前缀消息均可查询，消费状态正常；停止造数后确认缓冲清空，再按测试部署方式停服务。

消息在事务提交后确认，语义为至少一次，可能重复，不是 exactly-once。不得对共享测试环境执行全局 prune 或删除其他人的主题/队列。历史真实环境验收记录留在私有备份，不随公共源码发布。
