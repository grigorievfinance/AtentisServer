# Логирование и мониторинг

## Логи

Serilog, каталог `%ProgramData%\AtentisServer\logs` (переопределяется переменной `ATENTIS_DATA_DIR`):

| Файл | Формат | Хранение | Для чего |
|---|---|---|---|
| `atentis-YYYYMMDD.log` | текст | 30 дней, до 200 МБ на файл | читать глазами (`Get-Content -Tail 100 -Wait`) |
| `atentis-YYYYMMDD.json` | Compact JSON (`@t`, `@l`, `@m`, `@x`, свойства `Slot`, `Table`…) | 14 дней | Loki / ELK / `jq` |
| `bootstrap-*.log` | текст | 7 дней | ошибки до инициализации основного логгера (например, неверная конфигурация) |
| Журнал Windows → Application, источник `AtentisServer` | | | **только** предупреждения и ошибки: видны в Event Viewer, Windows Admin Center, Zabbix, Splunk и т.п. |

Уровни: `INF` — штатные события (запуск, подключение, сколько сделок сохранено), `WRN` — пропущенные строки, повторное
подключение, `ERR` — сбои записи/слота, `FTL` — аварийное завершение. Шум библиотеки Atentis (ошибка «Reopen» раз в секунду
при недоступном сервере) ограничен одной записью в минуту с счётчиком повторов. Уровни меняются в `appsettings.json`
(`Serilog:MinimumLevel`), например `Debug` — для детального лога Atentis (временно).

## Метрики Prometheus (`http://127.0.0.1:9464/metrics`)

| Метрика | Смысл |
|---|---|
| `atentis_slot_connected{slot}` | 1 — слот подключён |
| `atentis_slot_reconnects{slot}` | сколько раз слот пересоздавался |
| `atentis_table_updates_total{slot,table}`, `atentis_table_last_update_timestamp_seconds` | поток обновлений от Atentis |
| `atentis_rows_received_total`, `atentis_rows_stored_total` | прочитано / реально записано новых строк |
| `atentis_rows_skipped_total{slot,table,reason}` | строки, отброшенные из-за некорректных данных |
| `atentis_db_up`, `atentis_db_errors_total{target}`, `atentis_db_write_duration_seconds` | состояние и скорость PostgreSQL |
| `atentis_processing_errors_total`, `atentis_pending_tables` | ошибки и очередь обработки |
| `process_*`, `dotnet_*` | память, CPU, GC процесса |

Health-check: `/health/live` (процесс жив), `/health/ready` (200 — норма или `Degraded`: слот без связи; 503 — PostgreSQL недоступен).

## Grafana (рекомендуемый вариант)

Готовое в [`deploy/monitoring`](../deploy/monitoring):

1. **Prometheus** (`prometheus.yml`) собирает `atentis` (порт 9464) и системные метрики Windows
   ([windows_exporter](https://github.com/prometheus-community/windows_exporter), порт 9182, включая состояние службы).
2. **Алерты** (`alerts.yml`, проверено `promtool`): служба не отвечает / перезапускается, слот без связи > 5 мин,
   БД недоступна/ошибки/медленная, пропуск строк, служба Windows не Running. Уведомления — через Alertmanager
   (Telegram, e-mail, Slack) или напрямую в Grafana Alerting.
3. **Grafana** (`docker-compose.yml`): источники Prometheus, **PostgreSQL (роль только для чтения)** и Loki подключаются
   автоматически (`provisioning/`), дашборд «Atentis Server» загружается из `grafana/dashboards/atentis.json`:
   состояние слотов и БД, сделок в минуту, пропуски и ошибки, p95 записи, память, таблица здоровья из PostgreSQL,
   сделки по часам, последние сделки, лог предупреждений.
4. **Логи → Loki**: на сервере с сервисом установить [Grafana Alloy](https://grafana.com/docs/alloy/latest/) с `alloy-config.alloy`
   (читает `atentis-*.json`).

```bash
cd deploy/monitoring
export GRAFANA_ADMIN_PASSWORD=… ATENTIS_PG_HOST=db.example.local:5432 ATENTIS_PG_READER_PASSWORD=…
docker compose up -d        # Grafana: http://localhost:3000 (папка Atentis)
```

Запасной контур без Prometheus: Grafana умеет читать **только PostgreSQL** — вид `atentis.v_collector_health`
(heartbeat раз в 15 с, `seconds_since_heartbeat`, `state`, `last_error`) и таблица `atentis.trades` дают панели «жив ли
сборщик» и «сколько сделок собрано» без дополнительных компонентов. Алерт Grafana на `seconds_since_heartbeat > 60`
сработает и при падении самой службы.

## Альтернативы Grafana

| Вариант | Когда выбрать |
|---|---|
| **Zabbix** | уже есть в компании; агент на Windows + шаблон «Windows by Zabbix agent», проверка `/health/ready` через HTTP-агент, события Application-лога по источнику `AtentisServer`, SQL-проверка `v_collector_health`. |
| **Prometheus + Alertmanager без Grafana** | нужны только уведомления: правила из `alerts.yml` и Telegram/e-mail. |
| **Healthchecks.io / Uptime Kuma** | минимальная схема «жив / не жив»: опрос `/health/ready` снаружи или dead-man's-switch (ping при каждой записи). Uptime Kuma — self-hosted, ставится за минуту. |
| **Seq** (datalust.co) | если важнее логи, чем метрики: Serilog пишет в Seq одной строкой (`WriteTo.Seq`), есть сигналы и алерты; бесплатен для одного пользователя. |
| **Elastic / OpenSearch + Kibana** | уже есть стек ELK; JSON-логи читаются Filebeat. |
| **Netdata** | быстрый системный мониторинг сервера с готовыми алертами, без настройки. |
| **Windows Admin Center / Event Viewer + Task Scheduler** | вообще без внешних систем: предупреждения и ошибки уже в журнале Application; задача планировщика может отправить письмо по событию. |
| **Azure Monitor / Datadog / New Relic** | если инфраструктура уже в облаке; метрики Prometheus принимают все. |

Минимальный набор «чтобы быть спокойным» без развёртывания нового: служба с авто-перезапуском (уже включено) +
Uptime Kuma/Healthchecks на `/health/ready` + алерт Grafana/Zabbix на `seconds_since_heartbeat`.
