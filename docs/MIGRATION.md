# Переезд из AtentisConnector

## Что куда переехало

| Было (`AtentisConnector`) | Стало (`AtentisServer`) |
|---|---|
| Окно ввода логина/пароля, `settings.json` | `appsettings.json` / `appsettings.local.json` (адреса, логины) + переменные окружения службы (пароли) |
| Слоты `MICEXSlot`, `FORTSSlot` | `Collector:Slots` — `MICEX`/`SPOT`, `FORTS`/`FUT` (имя слота пишется в `slot_id`, рынок — в `market`) |
| SQLite `C:\AtentisData\database\Database.db`, CSV | PostgreSQL, схема `atentis` |
| `trades(TradeNo, OrderNo, TimeDeal, Symbol, SecSymbol, Price, Quantity, …)` | `atentis.trades` — `real_trade_no`, `order_no`, `trade_time`, `sec_code` (=`Symbol`), `sec_board` (=`SecSymbol`), `price`, `value`, `quantity`, `commission`, `clearing_commission`, `exchange_commission`, `account` … |
| `securities(DateTime, Symbol, Bid, …)` | `atentis.quotes` (по умолчанию выключено) |
| CSV ORDERS/POSITIONS/FIRM_HOLDING_TOTAL | `atentis.table_rows` (jsonb, история изменений) и вид `atentis.v_table_rows_latest` |
| «Финрез», пары спот/фьючерс, `tickers.txt`, `counts.txt` | **удалено** — считать отдельно по данным PostgreSQL |

**Важно:** в старых SQLite/SQL Server/ClickHouse в колонку `Price` попадал объём (`VALUE`), в PostgreSQL — цена (`PRICE`);
в новой схеме обе величины хранятся раздельно: `price` и `value`.

## Перенос накопленных данных (по желанию)

Исторические данные старой версии в новую БД автоматически не переносятся: в SQLite они хранились с другой семантикой
цены (см. выше) и без `account`. Если нужно — загрузить сделки можно скриптом по номерам сделок (`TradeNo` старой базы =
`real_trade_no`); при необходимости договоримся, как трактовать `Price`. Сами сделки всё равно заново соберутся
с сервера Atentis при первом запуске (таблица `TRADES` содержит сделки текущей торговой сессии).

## Как отозвать доступ Claude к старому репозиторию и сохранить его «для истории»

Старый репозиторий не изменялся: доступ к нему был только на чтение.

1. **Отозвать доступ приложения** — GitHub → *Settings* → *Applications* → вкладка **Installed GitHub Apps** → **Claude**
   → **Configure** → в блоке *Repository access* убрать `AtentisConnector` из списка выбранных репозиториев (оставить
   `AtentisServer`) → **Save**. Или, если приложение больше не нужно, удалить его целиком (*Uninstall*).
   Прямая ссылка для личного аккаунта: https://github.com/settings/installations
2. Для организации — то же через *Organization settings → Installed GitHub Apps → Claude → Configure*.
3. Если вы также авторизовали Claude как OAuth-приложение: *Settings → Applications → Authorized OAuth Apps → Claude → Revoke*.
4. В интерфейсе Claude: *Settings → Connectors* (GitHub) — можно временно отключить интеграцию.
5. **Сохранить репозиторий для истории**: `AtentisConnector` → *Settings* → *General* → *Danger Zone* →
   **Archive this repository**. Репозиторий станет доступным только для чтения всем (включая вас) — никто, включая будущие
   сессии, не сможет случайно изменить его; архивацию можно снять обратно.
6. В README старого репозитория (до архивации) удобно добавить строку «Проект перенесён в grigorievfinance/AtentisServer».
   Я этого **не делал** — вы просили не менять старый репозиторий.
7. Проверить, что доступа нет: *Settings → Applications → Claude → Configure* — в списке только нужные репозитории.

**История репозитория содержит пароли** (см. [SECURITY.md](SECURITY.md)): архивация их не скрывает. Смените пароли; если
репозиторий публичный — рассмотрите перевод в приватный (*Change visibility → Private*) или чистку истории (`git filter-repo`).
