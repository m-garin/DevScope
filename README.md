# DevScope

Локальный обзор работы сотрудника по данным YouTrack и GitLab.

Главный факт активности — worklog: задача попадает в отчёт по автору списания, даже если сотрудник не является её разработчиком или исполнителем. GitLab дополняет задачу ветками, коммитами, MR и обсуждениями, но не пересчитывается в часы.

## Быстрый запуск

Требуется Docker Desktop с Linux-контейнерами.

```powershell
Copy-Item .env.example .env
Copy-Item config/mcp.mock.json config/mcp.json
Copy-Item config/teams.example.json config/teams.json
docker compose up --build -d
```

Открыть: http://localhost:8080

По умолчанию используется безопасный режим `Mock`.

Compose также поднимает отдельный Streamable HTTP MCP-сервер на `http://localhost:8090/mcp`. Он эмулирует YouTrack и GitLab tools и читает данные из `data/mock-data.json`.

## Конфигурация MCP

`config/mcp.json` принимает объект `mcp` в формате OpenCode. Файл монтируется в контейнер только для чтения и исключён из Git. Токены не выводятся API и не должны попадать в скриншоты, логи или репозиторий.

## Текущее ограничение YouTrack MCP

Обследованный MCP умеет создавать списание через `youtrack_log_work`, но не умеет читать существующие work items. Поэтому:

- mock-режим полностью моделирует выборку по автору worklog и датам;
- реальный адаптер сможет получить задачи и их суммарные custom fields;
- точная выборка часов сотрудника за период потребует нового read-only MCP tool.

Рекомендуемый контракт шлюза:

```text
youtrack_search_work_items(
  projectKey,
  authorLogin,
  dateFrom,
  dateTo,
  offset,
  limit)
```

Минимальный результат: `id`, `issueId`, `author`, `date`, `durationMinutes`, `type`, `description`, `attributes` и пагинация.

## Разработка без Docker

Frontend:

```powershell
cd src/DevScope.Web
npm install
npm run dev
```

Backend требует .NET 10 SDK и запускается на `http://localhost:5080`.

