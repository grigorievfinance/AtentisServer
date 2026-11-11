-- Выполнить один раз под суперпользователем PostgreSQL (psql -U postgres -f setup-roles.sql).
-- Замените пароли! Не храните их в репозитории.

-- 1. Рабочая БД и роль сервиса сбора. Сервис владеет только своей БД и создаёт в ней схему atentis (AutoMigrate).
CREATE ROLE atentis_app LOGIN PASSWORD 'CHANGE_ME_APP';
CREATE DATABASE atentis OWNER atentis_app ENCODING 'UTF8';
REVOKE ALL ON DATABASE atentis FROM PUBLIC;

-- 2. Роль только для чтения - для Grafana и аналитиков (финрез и прочие расчёты теперь делаются ОТДЕЛЬНО от сборщика).
CREATE ROLE atentis_reader LOGIN PASSWORD 'CHANGE_ME_READER';
GRANT CONNECT ON DATABASE atentis TO atentis_reader;

\connect atentis
-- Выполнять после первого запуска сервиса (когда схема atentis уже создана миграцией) либо сразу:
CREATE SCHEMA IF NOT EXISTS atentis AUTHORIZATION atentis_app;
GRANT USAGE ON SCHEMA atentis TO atentis_reader;
ALTER DEFAULT PRIVILEGES FOR ROLE atentis_app IN SCHEMA atentis GRANT SELECT ON TABLES TO atentis_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA atentis TO atentis_reader;
