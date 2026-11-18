# AtentisServer

Служба Windows, которая **только собирает данные** из торговой системы Atentis и **сохраняет их в PostgreSQL**.
Никаких расчётов (финрез, пары спот/фьючерс и т.п.) здесь нет — их делают отдельно, читая таблицы PostgreSQL.

Это переработанная версия проекта `AtentisConnector` (WinForms-приложение). Подробный разбор старого кода и
список исправленных ошибок — в [docs/ANALYSIS.md](docs/ANALYSIS.md).

| | |
|---|---|
| Платформа | .NET 8 Worker Service (LTS-линейка, совместима с Atentis.Connection), Windows Server 2019+ (также запускается на Linux для разработки) |
| Запуск | Служба Windows: автозапуск, авто-перезапуск при сбое. Логины и пароли вводятся в окне «Панель управления» |
| Установка | Один установщик `AtentisServer-Setup-x.y.z.exe`: ставит один пользователь, работают с программой другие |
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
SupervisorLoop: пароли из памяти → подключение слотов, реконнект, полная синхронизация раз в 5 мин
ControlServer:  именованный канал ← «Панель управления» (логины, пароли, настройки БД)
StatusLoop:     метрики Prometheus + atentis.collector_status
```

* Atentis отдаёт полное состояние таблицы, поэтому обновления схлопываются: ничего не теряется, а БД не блокирует поток библиотеки.
* Если PostgreSQL недоступен — запись повторяется каждые 5 с; после восстановления данные дозаписываются без дублей.
* **Переподключение:** слот без соединения дольше `ReconnectAfterSeconds` (60 с) пересоздаётся с паролем из памяти, повторные
  неудачи — с нарастающей паузой до 5 минут. Процесс из-за проблем со связью **не завершается** (иначе пароли пропали бы).
* **Неверный пароль:** Atentis отклонил логин/пароль → пароль забывается, подключение останавливается (повторы могут заблокировать
  вашу учётную запись у брокера) и в панели показано «Неверный логин или пароль».
* **Авария процесса** (редкая): служба Windows перезапускает её через 5 → 30 → 60 с; настройки сохранены в файле, пароли Atentis
  придётся ввести заново (панель покажет «Ждёт пароль»).
* При остановке службы накопленное дозаписывается, слоты закрываются штатно (не `Process.Kill`).

## Установка (Windows Server 2019)

Нужны: сервер PostgreSQL 12+ (на этом или другом хосте). Ставить .NET на сервер **не нужно**.

**1. Получить установщик** `AtentisServer-Setup-x.y.z.exe`: вкладка *Releases* репозитория (после создания тега `vX.Y.Z`)
или артефакт `AtentisServer-Setup` у последней успешной сборки во вкладке *Actions*. Либо собрать самому на машине с
.NET SDK 8.0 и [Inno Setup 6](https://jrsoftware.org/isdl.php): `.\deploy\publish.ps1` → `publish\installer\`.

**2. Установить** (пользователь-администратор). Мастер спросит, кто будет работать с программой: укажите пользователей
Windows (по умолчанию — вы). Они войдут в группу `AtentisServerOperators` и смогут вводить логины и пароли **без прав
администратора**. Тихая установка:

```powershell
.\AtentisServer-Setup-4.1.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /OPERATORS="SERVER\ivanov,SERVER\petrov"
```

Установщик копирует службу и панель, регистрирует службу `AtentisServer` (отложенный автозапуск, авто-перезапуск,
отдельная учётная запись без прав входа), закрывает доступ к `C:\ProgramData\AtentisServer` и запускает службу.

**3. База данных (администратор БД, один раз):** создать базу и пользователя
([`deploy/postgres/setup-roles.sql`](deploy/postgres/setup-roles.sql), замените пароли).

**4. Пользователь запускает «Панель управления Atentis Server»** (ярлык на рабочем столе / в меню «Пуск») и вводит:
адрес/порт/логин/**пароль** Atentis для каждого рынка и параметры базы данных → **«Проверить подключение»** →
**«Сохранить и подключить»**. Панель показывает состояние каждого рынка и базы простыми словами.

* Всё, кроме паролей Atentis, сохраняется в файле `C:\ProgramData\AtentisServer\appsettings.local.json` (его читает служба при
  каждом запуске; редактировать руками не обязательно).
* **Пароли Atentis нигде не сохраняются**, только в памяти службы. После перезагрузки сервера (или перезапуска службы)
  служба сама стартует, но ждёт пароли: панель покажет «Ждёт пароль» — их нужно ввести снова. Поэтому по умолчанию включено
  «Показывать панель при входе пользователя в Windows».
* Новый пользователь: `net localgroup AtentisServerOperators ИМЯ /add`; ему нужно выйти из Windows и войти снова.
* Изменение адреса/порта/логина рынка применяется сразу (служба не перезапускается); для него пароль вводится заново.

Удаление: «Программы и компоненты» → Atentis Server. Настройки и журналы остаются в `C:\ProgramData\AtentisServer`, данные — в PostgreSQL.

Проверка:

```powershell
Get-Service AtentisServer
Invoke-RestMethod http://127.0.0.1:9464/health/ready
Get-Content "$env:ProgramData\AtentisServer\logs\atentis-*.log" -Tail 50
```

### Чек-лист первого запуска (проверьте на реальных слотах)

1. В панели оба рынка «Подключено», база «подключена»; `Get-Service AtentisServer` → `Running`; `…/health/ready` → `Healthy`.
2. В логе: `Слот MICEX подключён, запрашиваю полную синхронизацию таблиц`, затем `сохранено новых сделок: N`.
3. `SELECT slot_id, state, connected, seconds_since_heartbeat FROM atentis.v_collector_health;` — `Connected`, heartbeat < 30 с.
4. `SELECT count(*), min(trade_time), max(trade_time) FROM atentis.trades;` — сверить с сделками в терминале.
5. Остановить `Stop-Service` и снова `Start-Service` — дубликатов в `atentis.trades` быть не должно.
6. Принудительно убить процесс (`Stop-Process -Name AtentisServer -Force`) — через ~5 с служба поднимается сама; панель покажет
   «Ждёт пароль» (так и задумано: пароли хранились только в памяти) — введите пароли, сбор продолжится без дублей.
7. Обрыв связи: отключите на минуту сеть до сервера Atentis — рынок покажет «Связь потеряна, переподключаюсь…» и сам вернётся в
   «Подключено» без ввода пароля.

Обновление программы: запустить новый установщик поверх старого (служба останавливается на время обновления — пароли Atentis
придётся ввести заново; настройки сохраняются).

## Конфигурация

Обычно настройки не нужно править руками: их вводят в «Панели управления». Для администратора: порядок источников
(последний побеждает) — `appsettings.json` рядом с программой (значения по умолчанию) → переменные окружения →
**файл настроек `%ProgramData%\AtentisServer\appsettings.local.json`** → аргументы командной строки. В файл можно вручную
добавлять расширенные параметры (ниже) — служба при сохранении из панели их не затрагивает. Пароли Atentis в файл не
записываются и из файла не читаются. Другое расположение каталога данных — переменная `ATENTIS_DATA_DIR`.

| Параметр | По умолчанию | Описание |
|---|---|---|
| `ConnectionStrings:Postgres` | — | Подключение к PostgreSQL (пишет панель; для удалённой БД выбирайте шифрование «Обязательно + проверка сертификата») |
| `Collector:Slots[i]:Server/Port/Login` | — | Подключение к Atentis (пароль — только в панели); `PublicKeyFile` — защищённый режим |
| `Collector:Tables[]` | TRADES, ORDERS, POSITIONS, FIRM_HOLDING_TOTAL | Какие таблицы собирать; `KeyFields` — ключ строки для истории |
| `Collector:ReconnectAfterSeconds` | 60 | Через сколько секунд без связи пересоздавать слот |
| `Collector:FullSyncIntervalSeconds` | 300 | Страховочная полная синхронизация (0 = выкл) |
| `Monitoring:Url` | `http://127.0.0.1:9464` | Метрики и health-check (по умолчанию только localhost) |


## Разработка и тесты

```bash
dotnet build
dotnet test                                    # 83 теста (служба 64 + панель 19); интеграционные с PostgreSQL пропускаются без переменной ниже
ATENTIS_TEST_PG="Host=localhost;Database=atentis_test;Username=…;Password=…" dotnet test   # БД должна быть пустой тестовой!
```

Структура: `src/AtentisServer` (служба), `src/AtentisServer.Panel` (окно, Avalonia), `src/AtentisServer.Contracts` (протокол
службы↔панели), `installer/` (Inno Setup и проверка установки), `deploy/` (сборка, роли PostgreSQL, мониторинг).
Окно панели проверяется тестами без экрана (снимки: `PANEL_SCREENSHOT_DIR=… dotnet test tests/AtentisServer.Panel.Tests`).
CI на настоящем Windows собирает установщик и проходит установку → настройка → авария → удаление.
Для запуска службы на Linux/macOS: `dotnet run --project src/AtentisServer` (канал управления — Unix-сокет, панель — `dotnet run --project src/AtentisServer.Panel`).

## Документация

* [docs/ANALYSIS.md](docs/ANALYSIS.md) — что делал старый проект, найденные ошибки и уязвимости, что изменено;
* [docs/SECURITY.md](docs/SECURITY.md) — модель безопасности, секреты, права, что сделать немедленно;
* [docs/MONITORING.md](docs/MONITORING.md) — Grafana/Prometheus/Loki и альтернативы;
* [docs/MIGRATION.md](docs/MIGRATION.md) — переезд из `AtentisConnector`, перенос данных, отзыв доступа к старому репозиторию;
* [docs/Atentis.Connection-API-8.0.doc](docs/Atentis.Connection-API-8.0.doc) — документация библиотеки Atentis.Connection.

Библиотека `lib/Atentis.Connection.dll` (8.0.160.47, сборка под .NET 7 / AnyCPU) — сторонняя, лицензия Atentis.
