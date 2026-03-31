# API Notes

Current startup and migration behavior:

- Database migrations are not auto-applied on startup.
- Apply migrations manually with one of these options:
  - CLI: `dotnet-ef database update --project src/api/DmarcAnalyzer.Api.csproj --startup-project src/api/DmarcAnalyzer.Api.csproj`
  - HTTP endpoint: `POST /api/v1/admin/database/migrate`

Primary initial endpoints:

- `GET /api/v1/system/status`
- `GET /api/v1/clients`
- `GET /api/v1/clients/{id}`
- `POST /api/v1/clients`
- `PATCH /api/v1/clients/{id}`
- `GET /api/v1/domains`
- `GET /api/v1/domains/{id}`
- `POST /api/v1/domains`
- `PATCH /api/v1/domains/{id}`
- `GET /api/v1/mailbox-sources`
- `POST /api/v1/mailbox-sources`
- `PATCH /api/v1/mailbox-sources/{id}`
