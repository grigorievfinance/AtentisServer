# AtentisServer

Служба Windows, которая **только собирает данные** из торговой системы Atentis и **сохраняет их в PostgreSQL**.
Никаких расчётов (финрез, пары спот/фьючерс и т.п.) здесь нет — их делают отдельно, читая таблицы PostgreSQL.

Это переработанная версия проекта `AtentisConnector` (WinForms-приложение). Подробный разбор старого кода и
список исправленных ошибок — в [docs/ANALYSIS.md](docs/ANALYSIS.md).

| | |
|---|---|
| Платформа | .NET 8 Worker Service (LTS-линейка, совместима с Atentis.Connection), Windows Server 2019+ (также запускается на Linux для разработки) |
| Запуск | Служба Windows: автозапуск, авто-перезапуск при сбое, без интерфейса и без ручных действий |
| Данные | Atentis (`Atentis.Connection` 8.0, слоты MICEX и FORTS) → PostgreSQL |
| Мониторинг | Prometheus `/metrics`, health-check, таблица статуса в PostgreSQL, структурированные логи → Grafana (+ Loki) |

## Что собирается

| Таблица Atentis | Куда пишется | Как |
|---|---|---|
| `TRADES` | `atentis.trades` | идемпотентно (`UNIQUE` + `ON CONFLICT DO NOTHING`), пакетами |
| `ORDERS`, `POSITIONS`, `FIRM_HOLDING_TOTAL` | `atentis.table_rows` (jsonb, история изменений) | новая запись только если строка изменилась |
| `SECURITIES` (выключено по умолчанию) | `atentis.quotes` (bid/ask/last) | только изменения, не чаще `MinIntervalSeconds` |
| состояние слотов | `atentis.collector_status`, вид `atentis.v_collector_health` | раз в 15 с |

Схема — [`Storage/Sql/001_init.sql`](src/AtentisServer/Storage/Sql/001_init.sql), применяется автоматически при старте
(можно отключить: `Database:AutoMigrate=false`). Время хранится в `timestamptz` (UTC); время Atentis считается
московским (`Collector:ServerTimeZone`).

## Как это работает

```
Atentis (слот MICEX, слот FORTS)
   │  события библиотеки (чужие потоки: только пометить таблицу «грязной», ничего не блокируя)
   ▼
DirtyTracker ──► CollectorWorker.ProcessLoop (один поток) ──► снимок таблицы ──► Mapper ──► PostgresStore ──► PostgreSQL
                       ▲                                                   (пропуск битых строк, дедупликация)
SupervisorLoop: реконнект слотов, полная синхронизация раз в 5 мин, аварийный выход → перезапуск службой
StatusLoop:     метрики Prometheus + atentis.collector_status
```

* Atentis отдаёт полное состояние таблицы, поэтому обновления схлопываются: ничего не теряется, а БД не блокирует поток библиотеки.
* Если PostgreSQL недоступен — запись повторяется каждые 5 с; после восстановления данные дозаписываются без дублей.
* Слот без соединения дольше `ReconnectAfterSeconds` пересоздаётся; после `MaxConsecutiveReconnectFailures` неудач подряд
  процесс завершается с кодом 1, и служба Windows перезапускает его (5 с → 30 с → 60 с).
* При остановке службы накопленное дозаписывается, слоты закрываются штатно (не `Process.Kill`).

## Быстрый старт (Windows Server 2019)

Нужны: сервер PostgreSQL 12+ (на этом или другом хосте) и PowerShell 5.1 от имени администратора.

```powershell
# 1. На машине с .NET SDK 8.0 (или новее): собрать самодостаточный релиз (на сервер .NET ставить не нужно)
.\deploy\publish.ps1

# 2. В PostgreSQL (под postgres): создать БД и роли — отредактируйте пароли в файле
psql -U postgres -f deploy\postgres\setup-roles.sql

# 3. На сервере: установить службу (спросит строку подключения к БД и пароли Atentis)
.\deploy\install-service.ps1 -SourceDir .\publish\AtentisServer

# 4. Адреса/логины Atentis (не пароли!) — в %ProgramData%\AtentisServer\appsettings.local.json, затем:
Restart-Service AtentisServer
```

`appsettings.local.json` (пример) — переопределяет значения из `appsettings.json`:

```json
{
  "Collector": {
    "Slots": [
      { "Name": "MICEX", "Market": "SPOT", "Server": "10.0.0.1", "Port": 7800, "Login": "user1" },
      { "Name": "FORTS", "Market": "FUT",  "Server": "10.0.0.2", "Port": 7800, "Login": "user2" }
    ]
  }
}
```

Проверка:

```powershell
Get-Service AtentisServer
Invoke-RestMethod http://127.0.0.1:9464/health/ready
Get-Content "$env:ProgramData\AtentisServer\logs\atentis-*.log" -Tail 50
```

### Чек-лист первого запуска (проверьте на реальных слотах)

1. `Get-Service AtentisServer` → `Running`; `…/health/ready` → `Healthy`, а не `Degraded` (оба слота подключены).
2. В логе: `Слот MICEX подключён, запрашиваю полную синхронизацию таблиц`, затем `сохранено новых сделок: N`.
3. `SELECT slot_id, state, connected, seconds_since_heartbeat FROM atentis.v_collector_health;` — `Connected`, heartbeat < 30 с.
4. `SELECT count(*), min(trade_time), max(trade_time) FROM atentis.trades;` — сверить с сделками в терминале.
5. Остановить `Stop-Service` и снова `Start-Service` — дубликатов в `atentis.trades` быть не должно.
6. Принудительно убить процесс (`Stop-Process -Name AtentisServer -Force`) — через ~5 с служба должна подняться сама.

Обновление: остановить не нужно — `install-service.ps1` сам останавливает службу, копирует файлы и запускает её;
`-SkipSecrets` оставляет прежние пароли. Удаление: `deploy\uninstall-service.ps1`.

## Конфигурация

Порядок (последнее побеждает): `appsettings.json` → `%ProgramData%\AtentisServer\appsettings.local.json` →
переменные окружения → аргументы командной строки. Пароли и строка подключения хранятся **только в переменных окружения
службы** (реестр, доступ администраторам/SYSTEM) — их записывает `deploy\Set-AtentisSecrets.ps1`.

| Параметр | По умолчанию | Описание |
|---|---|---|
| `ConnectionStrings:Postgres` | — | `Host=…;Database=atentis;Username=atentis_app;Password=…;SSL Mode=VerifyFull` (для удалённой БД — шифрование!) |
| `Collector:Slots[i]:Server/Port/Login/Password` | — | Подключение к Atentis; `PublicKeyFile` — защищённый режим |
| `Collector:Tables[]` | TRADES, ORDERS, POSITIONS, FIRM_HOLDING_TOTAL | Какие таблицы собирать; `KeyFields` — ключ строки для истории |
| `Collector:ReconnectAfterSeconds` | 120 | Через сколько секунд без связи пересоздавать слот |
| `Collector:MaxConsecutiveReconnectFailures` | 10 | После скольких неудач завершить процесс для перезапуска службой |
| `Collector:FullSyncIntervalSeconds` | 300 | Страховочная полная синхронизация (0 = выкл) |
| `Monitoring:Url` | `http://127.0.0.1:9464` | Метрики и health-check (по умолчанию только localhost) |

Переменные окружения: `Collector__Slots__0__Password=…`, `ConnectionStrings__Postgres=…`.

## Разработка и тесты

```bash
dotnet build
dotnet test                                    # 39 тестов; интеграционные с PostgreSQL пропускаются без переменной ниже
ATENTIS_TEST_PG="Host=localhost;Database=atentis_test;Username=…;Password=…" dotnet test   # БД должна быть пустой тестовой!
```

Для запуска на Linux/macOS: `ConnectionStrings__Postgres=… Collector__Slots__0__Server=… dotnet run --project src/AtentisServer`.

## Документация

* [docs/ANALYSIS.md](docs/ANALYSIS.md) — что делал старый проект, найденные ошибки и уязвимости, что изменено;
* [docs/SECURITY.md](docs/SECURITY.md) — модель безопасности, секреты, права, что сделать немедленно;
* [docs/MONITORING.md](docs/MONITORING.md) — Grafana/Prometheus/Loki и альтернативы;
* [docs/MIGRATION.md](docs/MIGRATION.md) — переезд из `AtentisConnector`, перенос данных, отзыв доступа к старому репозиторию;
* [docs/Atentis.Connection-API-8.0.doc](docs/Atentis.Connection-API-8.0.doc) — документация библиотеки Atentis.Connection.

Библиотека `lib/Atentis.Connection.dll` (8.0.160.47, сборка под .NET 7 / AnyCPU) — сторонняя, лицензия Atentis.
