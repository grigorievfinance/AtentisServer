-- AtentisServer: начальная схема.
-- Все метки времени хранятся в timestamptz (UTC). Деньги и цены - numeric, не double.

CREATE SCHEMA IF NOT EXISTS atentis;

-- Сделки (таблица TRADES Atentis). Одна строка = одна сторона сделки клиента.
-- Таблица TRADES в Atentis не имеет ключевых полей, поэтому уникальность задаём здесь:
-- идемпотентность записи гарантирует ON CONFLICT DO NOTHING, а не SELECT перед каждой вставкой.
CREATE TABLE IF NOT EXISTS atentis.trades (
    id                   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    slot_id              text           NOT NULL,           -- MICEX / FORTS (имя слота из конфигурации)
    market               text           NOT NULL,           -- SPOT / FUT
    trade_no             bigint         NOT NULL,           -- TradeNo
    real_trade_no        bigint         NOT NULL,           -- RealTradeNo (номер сделки на бирже)
    order_no             bigint         NOT NULL,           -- OrderNo
    trade_time           timestamptz    NOT NULL,           -- TradeTime
    sec_board            text           NOT NULL,           -- SecBoard (режим торгов)
    sec_code             text           NOT NULL,           -- SecCode (тикер)
    buy_sell             char(1)        NOT NULL,           -- B / S
    quantity             bigint         NOT NULL,           -- лоты
    price                numeric(28,10) NOT NULL,           -- Price
    value                numeric(28,10) NOT NULL,           -- Value (объём в рублях)
    commission           numeric(28,10) NOT NULL DEFAULT 0, -- Commission
    clearing_commission  numeric(28,10) NOT NULL DEFAULT 0, -- ClearingCenterComm
    exchange_commission  numeric(28,10) NOT NULL DEFAULT 0, -- ExchangeComm
    account              text           NOT NULL DEFAULT '',-- Account
    broker_ref           text,
    firm_id              text,
    user_id              text,
    trade_type           text,
    period               text,
    ext_ref              text,
    collected_at         timestamptz    NOT NULL DEFAULT now(),
    CONSTRAINT trades_uk UNIQUE (slot_id, real_trade_no, buy_sell, account)
);
CREATE INDEX IF NOT EXISTS trades_time_idx       ON atentis.trades (trade_time);
CREATE INDEX IF NOT EXISTS trades_code_time_idx  ON atentis.trades (sec_code, trade_time);
CREATE INDEX IF NOT EXISTS trades_order_idx      ON atentis.trades (order_no);
CREATE INDEX IF NOT EXISTS trades_collected_idx  ON atentis.trades (collected_at);

-- Котировки (SECURITIES): история bid/ask, пишется только при изменении и не чаще MinIntervalSeconds.
CREATE TABLE IF NOT EXISTS atentis.quotes (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    slot_id       text           NOT NULL,
    sec_board     text           NOT NULL,
    sec_code      text           NOT NULL,
    quote_time    timestamptz    NOT NULL,
    bid           numeric(28,10),
    bid_qty       numeric(28,4),
    ask           numeric(28,10),
    ask_qty       numeric(28,4),
    last_price    numeric(28,10),
    collected_at  timestamptz    NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS quotes_code_time_idx ON atentis.quotes (slot_id, sec_code, quote_time);

-- История изменений остальных таблиц (ORDERS, POSITIONS, FIRM_HOLDING_TOTAL):
-- новая строка пишется только если содержимое (row_hash) изменилось.
CREATE TABLE IF NOT EXISTS atentis.table_rows (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    slot_id       text        NOT NULL,
    table_name    text        NOT NULL,
    row_key       text        NOT NULL,
    row_hash      text        NOT NULL,
    data          jsonb       NOT NULL,
    collected_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT table_rows_uk UNIQUE (slot_id, table_name, row_key, row_hash)
);
CREATE INDEX IF NOT EXISTS table_rows_key_idx ON atentis.table_rows (slot_id, table_name, row_key, collected_at DESC);

CREATE OR REPLACE VIEW atentis.v_table_rows_latest AS
SELECT DISTINCT ON (slot_id, table_name, row_key)
       slot_id, table_name, row_key, data, collected_at
FROM   atentis.table_rows
ORDER  BY slot_id, table_name, row_key, collected_at DESC, id DESC;

-- Состояние сборщика: одна строка на слот, обновляется сервисом (для Grafana / алертов по SQL).
CREATE TABLE IF NOT EXISTS atentis.collector_status (
    slot_id              text PRIMARY KEY,
    market               text        NOT NULL,
    state                text        NOT NULL,
    connected            boolean     NOT NULL,
    last_connected_at    timestamptz,
    last_table_update_at timestamptz,
    reconnects           bigint      NOT NULL DEFAULT 0,
    last_error           text,
    app_version          text        NOT NULL,
    host                 text        NOT NULL,
    updated_at           timestamptz NOT NULL DEFAULT now()
);

CREATE OR REPLACE VIEW atentis.v_collector_health AS
SELECT s.slot_id,
       s.market,
       s.state,
       s.connected,
       s.reconnects,
       s.last_error,
       s.app_version,
       s.host,
       s.updated_at,
       EXTRACT(EPOCH FROM (now() - s.updated_at))::bigint          AS seconds_since_heartbeat,
       EXTRACT(EPOCH FROM (now() - s.last_table_update_at))::bigint AS seconds_since_table_update,
       (SELECT max(t.collected_at) FROM atentis.trades t WHERE t.slot_id = s.slot_id) AS last_trade_collected_at
FROM   atentis.collector_status s;
